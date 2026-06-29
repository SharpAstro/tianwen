using System;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// Raised when an RC-Astro CLI invocation fails: the executable is
    /// missing, the process exits non-zero, the NDJSON stream carries an
    /// <c>error</c> event, or no readable output FITS is produced.
    /// </summary>
    public sealed class RcAstroCliException : Exception
    {
        public RcAstroCliException(string message) : base(message)
        {
        }

        public RcAstroCliException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
