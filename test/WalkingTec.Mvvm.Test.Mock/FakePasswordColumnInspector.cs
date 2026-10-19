using System;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Test.Mock
{
    /// <summary>
    /// An <see cref="IPasswordColumnInspector"/> that returns a fixed answer (or throws) and counts how often it was asked.
    /// </summary>
    public class FakePasswordColumnInspector : IPasswordColumnInspector
    {
        private readonly PasswordColumnInfo _info;
        private readonly Exception _exception;

        public FakePasswordColumnInspector(PasswordColumnInfo info)
        {
            _info = info;
        }

        public FakePasswordColumnInspector(Exception exception)
        {
            _exception = exception;
        }

        /// <summary>Number of times <see cref="Inspect"/> was called.</summary>
        public int Calls { get; private set; }

        public PasswordColumnInfo Inspect(IDataContext dc, Type userType)
        {
            Calls++;
            if (_exception != null)
            {
                throw _exception;
            }
            return _info;
        }
    }
}
