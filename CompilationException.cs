using System;
using System.Collections.Generic;
using System.Linq;
using Roslyn.Compilers.CSharp;

namespace Executify.ScriptCs
{
  public class CompilationException : ApplicationException
	{
		public CompilationException(IEnumerable<Diagnostic> errors)
			:base(String.Join(Environment.NewLine, errors.Select(x => x.ToString())))
		{
			Errors = errors;
		}

		public IEnumerable<Diagnostic> Errors { get; private set; } 
	}
}
