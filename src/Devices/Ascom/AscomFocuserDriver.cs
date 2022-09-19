using System;

namespace Astap.Lib.Devices.Ascom
{
    public class AscomFocuserDriver : AscomDeviceDriverBase, IFocuserDriver
    {
        public AscomFocuserDriver(AscomDevice device) : base(device)
        {

        }

        public int Position {
            get => _comObject?.Position is int pos ? pos : -1;
            set
            {
                if (_comObject is not null)
                {
                    _comObject.Position = value;
                }
                else
                {
                    throw new InvalidOperationException("Cannot change focuser position");
                }
            }
        }
    }
}