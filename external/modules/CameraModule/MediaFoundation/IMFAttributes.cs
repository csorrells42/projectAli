using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
internal interface IMFAttributes
{
	[PreserveSig]
	int GetItem(in Guid guidKey, nint value);

	[PreserveSig]
	int GetItemType(in Guid guidKey, out int type);

	[PreserveSig]
	int CompareItem(in Guid guidKey, nint value, [MarshalAs(UnmanagedType.Bool)] out bool result);

	[PreserveSig]
	int Compare(IMFAttributes? theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);

	[PreserveSig]
	int GetUINT32(in Guid guidKey, out int value);

	[PreserveSig]
	int GetUINT64(in Guid guidKey, out long value);

	[PreserveSig]
	int GetDouble(in Guid guidKey, out double value);

	[PreserveSig]
	int GetGUID(in Guid guidKey, out Guid value);

	[PreserveSig]
	int GetStringLength(in Guid guidKey, out int length);

	[PreserveSig]
	int GetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int bufferSize, out int length);

	[PreserveSig]
	int GetAllocatedString(in Guid guidKey, out nint value, out int length);

	[PreserveSig]
	int GetBlobSize(in Guid guidKey, out int blobSize);

	[PreserveSig]
	int GetBlob(in Guid guidKey, nint buffer, int bufferSize, out int blobSize);

	[PreserveSig]
	int GetAllocatedBlob(in Guid guidKey, out nint buffer, out int size);

	[PreserveSig]
	int GetUnknown(in Guid guidKey, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? value);

	[PreserveSig]
	int SetItem(in Guid guidKey, nint value);

	[PreserveSig]
	int DeleteItem(in Guid guidKey);

	[PreserveSig]
	int DeleteAllItems();

	[PreserveSig]
	int SetUINT32(in Guid guidKey, int value);

	[PreserveSig]
	int SetUINT64(in Guid guidKey, long value);

	[PreserveSig]
	int SetDouble(in Guid guidKey, double value);

	[PreserveSig]
	int SetGUID(in Guid guidKey, in Guid value);

	[PreserveSig]
	int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);

	[PreserveSig]
	int SetBlob(in Guid guidKey, nint buffer, int bufferSize);

	[PreserveSig]
	int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? value);

	[PreserveSig]
	int LockStore();

	[PreserveSig]
	int UnlockStore();

	[PreserveSig]
	int GetCount(out int items);

	[PreserveSig]
	int GetItemByIndex(int index, out Guid guidKey, nint value);

	[PreserveSig]
	int CopyAllItems(IMFAttributes destination);
}
