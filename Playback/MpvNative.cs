using System.Runtime.InteropServices;

namespace FusePlayer.Playback;

internal static class MpvNative
{
    internal const string LibraryName = "libmpv-2.dll";

    internal enum EventId
    {
        None = 0,
        Shutdown = 1,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        VideoReconfig = 17,
        AudioReconfig = 18,
        Seek = 20,
        PlaybackRestart = 21,
        PropertyChange = 22,
        QueueOverflow = 24
    }

    internal enum Format
    {
        None = 0,
        String = 1,
        OsdString = 2,
        Flag = 3,
        Int64 = 4,
        Double = 5,
        Node = 6,
        NodeArray = 7,
        NodeMap = 8,
        ByteArray = 9
    }

    internal enum EndFileReason
    {
        Eof = 0,
        Stop = 2,
        Quit = 3,
        Error = 4,
        Redirect = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Event
    {
        internal EventId EventId;
        internal int Error;
        internal ulong ReplyUserdata;
        internal IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EndFileEvent
    {
        internal EndFileReason Reason;
        internal int Error;
        internal long PlaylistEntryId;
        internal long PlaylistInsertId;
        internal int PlaylistInsertNumEntries;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(IntPtr context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_wakeup(IntPtr context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_wait_event(IntPtr context, double timeout);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option_string(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property_string(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_get_property_string(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command(IntPtr context, IntPtr arguments);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_observe_property(
        IntPtr context,
        ulong replyUserdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        Format format);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_error_string(int error);
}
