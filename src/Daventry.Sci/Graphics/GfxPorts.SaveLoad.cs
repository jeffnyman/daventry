using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

public sealed partial class GfxPorts
{
    private static SciRect SyncRect(SaveSerializer s, SciRect rect)
    {
        rect.Top = s.SyncInt16((short)rect.Top);
        rect.Left = s.SyncInt16((short)rect.Left);
        rect.Bottom = s.SyncInt16((short)rect.Bottom);
        rect.Right = s.SyncInt16((short)rect.Right);
        return rect;
    }

    /// <summary>Port of Window::saveLoadWithSerializer. The window id leads, so loading creates the window from it.</summary>
    private static Window SyncWindow(SaveSerializer s, Window? window)
    {
        ushort id = s.SyncUInt16(window?.Id ?? 0);
        window ??= new Window(id);
        window.Top = s.SyncInt16((short)window.Top);
        window.Left = s.SyncInt16((short)window.Left);
        window.Rect = SyncRect(s, window.Rect);
        window.CurTop = s.SyncInt16((short)window.CurTop);
        window.CurLeft = s.SyncInt16((short)window.CurLeft);
        window.FontHeight = s.SyncInt16((short)window.FontHeight);
        window.FontId = s.SyncInt32(window.FontId);
        window.GreyedOutput = s.SyncBool(window.GreyedOutput);
        window.PenClr = s.SyncInt16((short)window.PenClr);
        window.BackClr = s.SyncInt16((short)window.BackClr);
        window.PenMode = s.SyncInt16((short)window.PenMode);
        window.CounterTillFree = s.SyncUInt16((ushort)window.CounterTillFree);
        window.Dims = SyncRect(s, window.Dims);
        window.RestoreRect = SyncRect(s, window.RestoreRect);
        window.WndStyle = s.SyncUInt16(window.WndStyle);
        window.SaveScreenMask = s.SyncUInt16(window.SaveScreenMask);
        if (s.IsLoading)
        {
            // the hunk table is not saved, so the saved backgrounds are gone
            window.Saved1 = RegT.Null;
            window.Saved2 = RegT.Null;
        }

        window.Title = s.SyncString(window.Title);
        window.Drawn = s.SyncBool(window.Drawn);
        return window;
    }

    /// <summary>
    /// Port of GfxPorts::saveLoadWithSerializer. Reset() runs before this on restore. Restored
    /// windows are kept by id but not put into the window list, so the kernel does not use them:
    /// their contents could only be repainted by the scripts, which do not (Camelot's map screen,
    /// bug #6744), and Sierra's interpreter did not show them after restoring either.
    /// </summary>
    public void SaveLoad(SaveSerializer s)
    {
        if (s.Version < 27)
        {
            return;
        }

        uint windowCount = 0;
        if (s.IsSaving)
        {
            for (int id = Port.FirstScriptWindowId; id < _windowsById.Count; id++)
            {
                if (_windowsById[id] is not null)
                {
                    windowCount++;
                }
            }
        }

        windowCount = s.SyncUInt32(windowCount);

        if (s.IsSaving)
        {
            for (int id = Port.FirstScriptWindowId; id < _windowsById.Count; id++)
            {
                if (_windowsById[id] is Window window)
                {
                    SyncWindow(s, window);
                }
            }
        }
        else
        {
            while (windowCount > 0)
            {
                var window = SyncWindow(s, null);
                while (_windowsById.Count <= window.Id)
                {
                    _windowsById.Add(null);
                }

                _windowsById[window.Id] = window;
                if (window.CounterTillFree != 0)
                {
                    _freeCounter++;
                }

                windowCount--;
            }
        }
    }
}
