using System;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace MoyuBox
{
    public sealed class TaskbarWindowGroup : IDisposable
    {
        public const string AppId = "MoyuBox.Desktop";
        readonly IntPtr window; readonly string previous; readonly uint owner;
        TaskbarWindowGroup(IntPtr handle,string original,uint process){window=handle;previous=original;owner=process;}
        [StructLayout(LayoutKind.Sequential)] struct PropertyKey {public Guid format;public uint id;}
        [StructLayout(LayoutKind.Explicit,Size=24)] struct Variant { [FieldOffset(0)]public ushort type;[FieldOffset(8)]public IntPtr pointer; }
        [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IPropertyStore {
            [PreserveSig]int GetCount(out uint count);
            [PreserveSig]int GetAt(uint index,out PropertyKey key);
            [PreserveSig]int GetValue(ref PropertyKey key,out Variant value);
            [PreserveSig]int SetValue(ref PropertyKey key,ref Variant value);
            [PreserveSig]int Commit();
        }
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SetCurrentProcessExplicitAppUserModelID(string id);
        [DllImport("shell32.dll")]static extern int SHGetPropertyStoreForWindow(IntPtr handle,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore store);
        [DllImport("ole32.dll")]static extern int PropVariantClear(ref Variant value);
        [DllImport("user32.dll")]static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr handle,out uint process);
        static PropertyKey Key(){return new PropertyKey{format=new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),id=5};}
        static IPropertyStore Store(IntPtr window){IPropertyStore store;var iid=typeof(IPropertyStore).GUID;Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window,ref iid,out store));return store;}
        public static void Initialize(){try{Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));}catch(Exception error){Telemetry.Caught("taskbar","host_group.failed",error);}}
        public static string ReadId(IntPtr window) {
            var store=Store(window);var key=Key();Variant value=new Variant();
            try{Marshal.ThrowExceptionForHR(store.GetValue(ref key,out value));if(value.type!=0&&value.type!=31)throw new InvalidOperationException("Unexpected AppUserModelID variant type");return value.type==31?Marshal.PtrToStringUni(value.pointer):null;}
            finally{PropVariantClear(ref value);Marshal.ReleaseComObject(store);}
        }
        static void WriteId(IntPtr window,string id) {
            var store=Store(window);var key=Key();var value=new Variant();
            try{if(id!=null){value.type=31;value.pointer=Marshal.StringToCoTaskMemUni(id);}Marshal.ThrowExceptionForHR(store.SetValue(ref key,ref value));Marshal.ThrowExceptionForHR(store.Commit());}
            finally{PropVariantClear(ref value);Marshal.ReleaseComObject(store);}
        }
        public static TaskbarWindowGroup Attach(Process process) {
            process.Refresh();IntPtr window=process.MainWindowHandle;if(window==IntPtr.Zero)return null;
            uint owner;GetWindowThreadProcessId(window,out owner);if(owner!=(uint)process.Id)throw new InvalidOperationException("Window is not owned by the launched process");
            string original=ReadId(window);WriteId(window,AppId);
            if(ReadId(window)!=AppId)throw new InvalidOperationException("Taskbar group verification failed");
            return new TaskbarWindowGroup(window,original,owner);
        }
        public void Dispose() {
            if(!IsWindow(window))return;
            uint currentOwner;GetWindowThreadProcessId(window,out currentOwner);if(currentOwner!=owner)return;
            // Do not overwrite a subsequent change made by the external application itself.
            if(ReadId(window)==AppId){WriteId(window,previous);if(ReadId(window)!=previous)throw new InvalidOperationException("Original taskbar group could not be verified");}
        }
    }
}
