using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security;
using System.Security.Permissions;
using System.Security.Policy;
using Common.Logging;
using Roslyn.Compilers;
using Roslyn.Compilers.CSharp;
using Roslyn.Scripting.CSharp;
using ScriptCs;

namespace Executify.ScriptCs
{
	public class ExecutifyEngine : IScriptEngine
	{
		private readonly ScriptEngine _scriptEngine;
		private readonly ILog _logger;
		private readonly IFileSystem _fileSystem;

		public const string SessionKey = "Session";
		private static readonly string AssemblyNamePrefix = "E-" + Guid.NewGuid() + "-v1";

		public ExecutifyEngine(ILog logger, IFileSystem fileSystem)
		{
			_scriptEngine = new ScriptEngine();
			_scriptEngine.AddReference(typeof(ScriptExecutor).Assembly);
			_logger = logger;
			_fileSystem = fileSystem;
		}

		public string BaseDirectory
		{
			get { return _scriptEngine.BaseDirectory; }
			set { _scriptEngine.BaseDirectory = value; }
		}

		public string FileName { get; set; }

		private SessionState<object> GetSession(IEnumerable<string> references, IEnumerable<string> namespaces, ScriptPackSession scriptPackSession)
		{
			var distinctReferences = references.Union(scriptPackSession.References).Distinct().Select(x => x.Replace(".dll", "")).ToList();
			var distinctNamespaces = namespaces.Union(scriptPackSession.Namespaces).Distinct().ToList();

			var sessionState = new SessionState<object> {
				References = distinctReferences,
				Namespaces = distinctNamespaces
			};

			scriptPackSession.State[SessionKey] = sessionState;

			return sessionState;
		}

		public virtual ScriptResult Execute(string code, string[] scriptArgs, IEnumerable<string> references, IEnumerable<string> namespaces, ScriptPackSession scriptPackSession)
		{
			Guard.AgainstNullArgument("scriptPackSession", scriptPackSession);

			_logger.Info("Starting to create execution components");
			_logger.Debug("Creating script host");

			var session = GetSession(references, namespaces, scriptPackSession);

			_logger.Info("Starting execution");

			var fileName = new FileInfo(FileName).Name + ".dll";
			var codeDir = Path.Combine(_fileSystem.CurrentDirectory, "code");
			var dllPath = Path.Combine(codeDir, fileName);
			var dllExists = _fileSystem.FileExists(dllPath);

			if (!_fileSystem.DirectoryExists(codeDir))
				_fileSystem.CreateDirectory(codeDir);

			var scriptResult = new ScriptResult();

			if (!dllExists) {
				_logger.Debug("Compiling submission");

				var referencesForCompilation = session.References.Union(new[] { "mscorlib", "System", "System.Core", "Microsoft.CSharp" }).Distinct().Select(MetadataReference.CreateAssemblyReference).ToList();
				var namespacesForCompilation = ReadOnlyArray<string>.CreateFrom(session.Namespaces);
				var parseOptions = new ParseOptions(CompatibilityMode.None, LanguageVersion.CSharp6, true, SourceCodeKind.Script);

				var syntaxTree = SyntaxTree.ParseText(code, options: parseOptions);

				var options = new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)
					.WithUsings(namespacesForCompilation.ToList());

				var compilation = Compilation.Create(AssemblyNamePrefix, options, new[] { syntaxTree }, referencesForCompilation);

				using (var exeStream = new MemoryStream()) {
					var result = compilation.Emit(exeStream);

					if (result.Success) {
						_logger.Debug("Compilation was successful.");
						var exeBytes = exeStream.ToArray();

						_fileSystem.WriteAllBytes(dllPath, exeBytes);
						dllExists = true;
					} else {
						var errors = String.Join(Environment.NewLine, result.Diagnostics.Select(x => x.ToString()));
						_logger.ErrorFormat("Error occurred when compiling: {0})", errors);

						scriptResult.CompileException = new CompilationException(result.Diagnostics);
					}
				}
			}

			if (dllExists) {
				_logger.Debug("Creating Executify Sandbox AppDomain");

				var evidence = new Evidence();
				evidence.AddHostEvidence(new Zone(SecurityZone.Untrusted));

				var permissions = SecurityManager.GetStandardSandbox(evidence);
				permissions.AddPermission(new SecurityPermission(SecurityPermissionFlag.Execution));
				permissions.AddPermission(new WebPermission(PermissionState.Unrestricted));
				permissions.AddPermission(new DnsPermission(PermissionState.Unrestricted));
				permissions.AddPermission(new NetworkInformationPermission(NetworkInformationAccess.Ping));

				var setup = AppDomain.CurrentDomain.SetupInformation;
				var domain = AppDomain.CreateDomain("ExecutifySandbox", evidence, setup, permissions, null);

				_logger.Debug("Retrieving compiled script method (reflection).");

				try {
					_logger.Debug("Invoking method.");
					Activator.CreateInstanceFrom(domain, dllPath, "Script");

					scriptResult.ReturnValue = null;
				} catch (Exception exc) {
					_logger.Error("An error occurred when executing the scripts.");
					_logger.ErrorFormat("Exception Message: {0} {1}Stack Trace:{2}",
						exc.InnerException.Message,
						Environment.NewLine,
						exc.InnerException.StackTrace);

					scriptResult.ExecuteException = exc;
				}
			}

			return scriptResult;
		}
	}
}
