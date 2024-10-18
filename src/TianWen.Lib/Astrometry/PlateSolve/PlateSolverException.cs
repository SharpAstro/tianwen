using System;
using System.Runtime.Serialization;

namespace TianWen.Lib.Astrometry.PlateSolve
{
    [Serializable]
    public class PlateSolverException : Exception
    {
        public PlateSolverException()
        {
        }

        public PlateSolverException(string? message) : base(message)
        {
        }

        public PlateSolverException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}