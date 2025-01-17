
using ACE.Entity.Enum.Properties;

namespace ACE.Server.WorldObjects
{
    partial class Container
    {
        public uint Viewer
        {
            get => GetProperty(PropertyInstanceId.Viewer) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInstanceId.Viewer); else SetProperty(PropertyInstanceId.Viewer, value); }
        }

        public bool ResetMessagePending
        {
            get => GetProperty(PropertyBool.ResetMessagePending) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.ResetMessagePending); else SetProperty(PropertyBool.ResetMessagePending, value); }
        }
    }
}
