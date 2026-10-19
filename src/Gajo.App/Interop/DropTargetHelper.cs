using System;
using System.Runtime.InteropServices;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Gajo.Interop
{
    /// <summary>
    /// Keeps Explorer's translucent "ghost" of the dragged file visible while it is over the wheel.
    /// Purely cosmetic: every call is wrapped so a failure never affects the drop.
    /// </summary>
    internal sealed class DropTargetHelper
    {
        [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDropTargetHelper
        {
            void DragEnter(IntPtr hwndTarget, ComIDataObject pDataObj, ref Native.POINT ppt, int dwEffect);
            void DragLeave();
            void DragOver(ref Native.POINT ppt, int dwEffect);
            void Drop(ComIDataObject pDataObj, ref Native.POINT ppt, int dwEffect);
            void Show([MarshalAs(UnmanagedType.Bool)] bool fShow);
        }

        [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
        private class DragDropHelperClass { }

        private IDropTargetHelper? _helper;

        private IDropTargetHelper? Helper
        {
            get
            {
                if (_helper == null)
                {
                    try { _helper = (IDropTargetHelper)new DragDropHelperClass(); }
                    catch { _helper = null; }
                }
                return _helper;
            }
        }

        public void DragEnter(IntPtr hwnd, object data, int effect)
        {
            try
            {
                if (data is ComIDataObject com && Helper is { } h)
                {
                    Native.GetCursorPos(out var pt);
                    h.DragEnter(hwnd, com, ref pt, effect);
                }
            }
            catch { }
        }

        public void DragOver(int effect)
        {
            try
            {
                if (Helper is { } h) { Native.GetCursorPos(out var pt); h.DragOver(ref pt, effect); }
            }
            catch { }
        }

        public void DragLeave()
        {
            try { Helper?.DragLeave(); } catch { }
        }

        public void Drop(object data, int effect)
        {
            try
            {
                if (data is ComIDataObject com && Helper is { } h)
                {
                    Native.GetCursorPos(out var pt);
                    h.Drop(com, ref pt, effect);
                }
            }
            catch { }
        }
    }
}
