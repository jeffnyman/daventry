namespace Daventry.Sci.Engine;

public sealed partial class SciArray
{
    /// <summary>Port of SciArray::saveLoadWithSerializer: type, element size, size and the elements.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        ushort savedSize = (ushort)Size;
        var type = (SciArrayType)s.SyncByte((byte)Type);
        byte elementSize = s.SyncByte((byte)(IsRaw ? 1 : 2));
        savedSize = s.SyncUInt16(savedSize);

        if (s.IsLoading)
        {
            if (type != SciArrayType.Invalid)
            {
                SetType(type);
            }

            _ = elementSize;
            Resize(savedSize, true);
        }

        switch (Type)
        {
            case SciArrayType.Int16:
            case SciArrayType.Id:
                for (int i = 0; i < savedSize; i++)
                {
                    _regs[i] = s.SyncReg(_regs[i]);
                }

                break;
            case SciArrayType.Byte:
            case SciArrayType.String:
                s.SyncBytes(_bytes, 0, savedSize);
                break;
            default:
                throw new SciVmException($"Attempt to sync invalid SciArray type {Type}");
        }
    }
}

public sealed partial class SciBitmap
{
    /// <summary>Port of SciBitmap::saveLoadWithSerializer: the GC flag and the raw bitmap bytes.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        if (s.Version < 36)
        {
            return;
        }

        ShouldGc = s.SyncBool(ShouldGc);
        uint dataSize = s.SyncUInt32((uint)_data.Length);
        if (s.IsLoading)
        {
            _data = new byte[dataSize];
        }

        s.SyncBytes(_data, 0, (int)dataSize);
    }
}
