using System.Runtime.InteropServices;

namespace Deguffer.Core.Wmi;

// WMI's own COM interfaces, declared as far as the last slot a caller here uses. Slot order is
// WbemCli.h's: a COM call goes to a slot by its position, so a slot left out or moved calls the
// wrong method with no error. A slot no caller uses is declared without parameters, as a place
// holder only, and must never be called.

[ComImport]
[Guid("dc12a687-737f-11cf-884d-00aa004b2e24")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemLocator
{
    [PreserveSig]
    int ConnectServer(
        [MarshalAs(UnmanagedType.BStr)] string networkResource,
        [MarshalAs(UnmanagedType.BStr)] string? user,
        [MarshalAs(UnmanagedType.BStr)] string? password,
        [MarshalAs(UnmanagedType.BStr)] string? locale,
        int securityFlags,
        [MarshalAs(UnmanagedType.BStr)] string? authority,
        [MarshalAs(UnmanagedType.IUnknown)] object? context,
        out IWbemServices services);
}

/// <summary>Declared as far as <c>ExecMethod</c>, the twenty-second slot after <c>IUnknown</c>'s.</summary>
[ComImport]
[Guid("9556dc99-828c-11cf-a37e-00aa003240c7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemServices
{
    [PreserveSig] int OpenNamespace();

    [PreserveSig] int CancelAsyncCall();

    [PreserveSig] int QueryObjectSink();

    [PreserveSig]
    int GetObject(
        [MarshalAs(UnmanagedType.BStr)] string objectPath,
        int flags,
        [MarshalAs(UnmanagedType.IUnknown)] object? context,
        out IWbemClassObject instance,
        IntPtr callResult);

    [PreserveSig] int GetObjectAsync();

    [PreserveSig] int PutClass();

    [PreserveSig] int PutClassAsync();

    [PreserveSig] int DeleteClass();

    [PreserveSig] int DeleteClassAsync();

    [PreserveSig] int CreateClassEnum();

    [PreserveSig] int CreateClassEnumAsync();

    [PreserveSig] int PutInstance();

    [PreserveSig] int PutInstanceAsync();

    [PreserveSig] int DeleteInstance();

    [PreserveSig] int DeleteInstanceAsync();

    [PreserveSig] int CreateInstanceEnum();

    [PreserveSig] int CreateInstanceEnumAsync();

    [PreserveSig]
    int ExecQuery(
        [MarshalAs(UnmanagedType.BStr)] string language,
        [MarshalAs(UnmanagedType.BStr)] string query,
        int flags,
        [MarshalAs(UnmanagedType.IUnknown)] object? context,
        out IEnumWbemClassObject instances);

    [PreserveSig] int ExecQueryAsync();

    [PreserveSig] int ExecNotificationQuery();

    [PreserveSig] int ExecNotificationQueryAsync();

    [PreserveSig]
    int ExecMethod(
        [MarshalAs(UnmanagedType.BStr)] string objectPath,
        [MarshalAs(UnmanagedType.BStr)] string methodName,
        int flags,
        [MarshalAs(UnmanagedType.IUnknown)] object? context,
        IWbemClassObject? inParameters,
        out IWbemClassObject? outParameters,
        IntPtr callResult);
}

/// <summary>Declared as far as <c>Next</c>, the second slot after <c>IUnknown</c>'s.</summary>
[ComImport]
[Guid("027947e1-d731-11ce-a357-000000000001")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumWbemClassObject
{
    [PreserveSig] int Reset();

    /// <summary>Asked for one object at a time, so the array it fills is a single pointer.</summary>
    [PreserveSig]
    int Next(int timeout, uint count, out IWbemClassObject instance, out uint returned);
}

/// <summary>Declared as far as <c>GetMethod</c>, the seventeenth slot after <c>IUnknown</c>'s.</summary>
[ComImport]
[Guid("dc12a681-737f-11cf-884d-00aa004b2e24")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemClassObject
{
    [PreserveSig] int GetQualifierSet();

    [PreserveSig]
    int Get(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        int flags,
        out object? value,
        out int type,
        out int flavour);

    /// <summary>The value travels as a <c>VARIANT*</c>, and a type of 0 takes the property's own.</summary>
    [PreserveSig]
    int Put(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        int flags,
        ref object value,
        int type);

    [PreserveSig] int Delete();

    [PreserveSig] int GetNames();

    [PreserveSig] int BeginEnumeration();

    [PreserveSig] int Next();

    [PreserveSig] int EndEnumeration();

    [PreserveSig] int GetPropertyQualifierSet();

    [PreserveSig] int Clone();

    [PreserveSig] int GetObjectText();

    [PreserveSig] int SpawnDerivedClass();

    [PreserveSig]
    int SpawnInstance(int flags, out IWbemClassObject instance);

    [PreserveSig] int CompareTo();

    [PreserveSig] int GetPropertyOrigin();

    [PreserveSig] int InheritsFrom();

    [PreserveSig]
    int GetMethod(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        int flags,
        out IWbemClassObject? inSignature,
        out IWbemClassObject? outSignature);
}
