using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

/// <summary>
/// AOT-safe wrapper around a COM IDispatch object.
/// Uses raw vtable function pointers — no reflection, no dynamic, no Type.InvokeMember.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class DispatchObject : IDisposable
{
    private nint _pDispatch;
    private readonly Dictionary<string, int> _dispIdCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Creates a COM object from a ProgID and obtains its IDispatch interface.
    /// </summary>
    public DispatchObject(string progId)
    {
        int hr = NativeMethods.CLSIDFromProgID(progId, out var clsid);
        Marshal.ThrowExceptionForHR(hr);

        hr = NativeMethods.CoCreateInstance(in clsid, 0, NativeMethods.CLSCTX_ALL, in NativeMethods.IID_IDispatch, out _pDispatch);
        Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>
    /// Wraps an existing IDispatch pointer (takes ownership, will Release on dispose).
    /// </summary>
    public DispatchObject(nint pDispatch)
    {
        if (pDispatch == 0) throw new ArgumentNullException(nameof(pDispatch));
        _pDispatch = pDispatch;
        // AddRef since we're taking ownership
        Marshal.AddRef(pDispatch);
    }

    #region Typed getters

    public bool GetBool(string name)
    {
        // ASCOM returns booleans as VT_BOOL (VARIANT_BOOL). Although its storage is a
        // 16-bit short, ComVariant.As<short>() refuses VT_BOOL because the variant type
        // tag is not VT_I2 — reading via As<bool>() uses the bool-specific path which
        // accepts VT_BOOL natively. (Needed by ASCOM Platform 7 drivers whose
        // `Connecting` property is reported as VT_BOOL, e.g. ASCOM OmniSim.)
        var variant = GetPropertyVariant(name);
        try
        {
            return variant.As<bool>();
        }
        finally
        {
            variant.Dispose();
        }
    }

    public int GetInt(string name) => GetProperty<int>(name);

    public short GetShort(string name) => GetProperty<short>(name);

    public double GetDouble(string name) => GetProperty<double>(name);

    public string GetString(string name)
    {
        var variant = GetPropertyVariant(name);
        try
        {
            return variant.As<string>() ?? string.Empty;
        }
        finally
        {
            variant.Dispose();
        }
    }

    public DateTime GetDateTime(string name)
    {
        var variant = GetPropertyVariant(name);
        try
        {
            return DateTime.FromOADate(variant.As<double>());
        }
        finally
        {
            variant.Dispose();
        }
    }

    public string[] GetStringArray(string name)
    {
        var variant = GetPropertyVariant(name);
        try
        {
            // ASCOM returns string arrays as SAFEARRAY of BSTR
            return variant.As<string[]>() ?? [];
        }
        finally
        {
            variant.Dispose();
        }
    }

    public int[] GetIntArray(string name)
    {
        var variant = GetPropertyVariant(name);
        try
        {
            return variant.As<int[]>() ?? [];
        }
        finally
        {
            variant.Dispose();
        }
    }

    public int[,] GetInt2DArray(string name)
    {
        var variant = GetPropertyVariant(name);
        try
        {
            return variant.As<int[,]>() ?? new int[0, 0];
        }
        finally
        {
            variant.Dispose();
        }
    }

    public object? GetObject(string name)
    {
        var variant = GetPropertyVariant(name);
        try
        {
            return variant.As<object>();
        }
        finally
        {
            variant.Dispose();
        }
    }

    #endregion

    #region Typed setters

    public void Set(string name, bool value) => SetProperty(name, ComVariant.Create((short)(value ? -1 : 0))); // VARIANT_TRUE = -1
    public void Set(string name, int value) => SetProperty(name, ComVariant.Create(value));
    public void Set(string name, short value) => SetProperty(name, ComVariant.Create(value));
    public void Set(string name, double value) => SetProperty(name, ComVariant.Create(value));
    public void Set(string name, string value) => SetProperty(name, ComVariant.Create(value));
    public void Set(string name, DateTime value) => SetProperty(name, ComVariant.Create(value.ToOADate()));

    #endregion

    #region Method invocation

    public void InvokeMethod(string name)
    {
        InvokeRaw(name, NativeMethods.DISPATCH_METHOD, []);
    }

    public void InvokeMethod(string name, params object[] args)
    {
        var variants = ArgsToVariants(args);
        try
        {
            InvokeRaw(name, NativeMethods.DISPATCH_METHOD, variants);
        }
        finally
        {
            DisposeVariants(variants);
        }
    }

    public bool InvokeMethodBool(string name, params object[] args)
    {
        var variants = ArgsToVariants(args);
        try
        {
            var result = InvokeRawWithResult(name, NativeMethods.DISPATCH_METHOD, variants);
            try { return result.As<bool>(); }  // VT_BOOL — see GetBool comment
            finally { result.Dispose(); }
        }
        finally
        {
            DisposeVariants(variants);
        }
    }

    public int InvokeMethodInt(string name, params object[] args)
    {
        var variants = ArgsToVariants(args);
        try
        {
            var result = InvokeRawWithResult(name, NativeMethods.DISPATCH_METHOD, variants);
            try { return result.As<int>(); }
            finally { result.Dispose(); }
        }
        finally
        {
            DisposeVariants(variants);
        }
    }

    public double InvokeMethodDouble(string name, params object[] args)
    {
        var variants = ArgsToVariants(args);
        try
        {
            var result = InvokeRawWithResult(name, NativeMethods.DISPATCH_METHOD, variants);
            try { return result.As<double>(); }
            finally { result.Dispose(); }
        }
        finally
        {
            DisposeVariants(variants);
        }
    }

    public object? InvokeMethodObject(string name, params object[] args)
    {
        var variants = ArgsToVariants(args);
        try
        {
            var result = InvokeRawWithResult(name, NativeMethods.DISPATCH_METHOD, variants);
            try { return result.As<object>(); }
            finally { result.Dispose(); }
        }
        finally
        {
            DisposeVariants(variants);
        }
    }

    /// <summary>
    /// Invokes a method that returns an IDispatch object (e.g. <c>AxisRates(axis)</c> → <c>IAxisRates</c>).
    /// Returns a new <see cref="DispatchObject"/> wrapping the sub-dispatch; the caller owns and must dispose it.
    /// </summary>
    public DispatchObject InvokeMethodDispatch(string name, params object[] args)
    {
        var variants = ArgsToVariants(args);
        try
        {
            var result = InvokeRawWithResult(name, NativeMethods.DISPATCH_METHOD, variants);
            try { return UnwrapDispatch(name, result); }
            finally { result.Dispose(); }
        }
        finally
        {
            DisposeVariants(variants);
        }
    }

    /// <summary>
    /// Reads a parameterized property that returns an IDispatch object (e.g. <c>Item(index)</c> on an
    /// ASCOM collection → <c>IRate</c>). Collection default properties use <see cref="NativeMethods.DISPATCH_PROPERTYGET"/>.
    /// </summary>
    public DispatchObject GetPropertyDispatch(string name, params object[] args)
    {
        var variants = ArgsToVariants(args);
        try
        {
            var result = InvokeRawWithResult(name, NativeMethods.DISPATCH_PROPERTYGET, variants);
            try { return UnwrapDispatch(name, result); }
            finally { result.Dispose(); }
        }
        finally
        {
            DisposeVariants(variants);
        }
    }

    private static DispatchObject UnwrapDispatch(string name, ComVariant variant)
    {
        if (variant.VarType is not (VarEnum.VT_DISPATCH or VarEnum.VT_UNKNOWN))
        {
            throw new InvalidOperationException(
                $"'{name}' returned VARIANT type {variant.VarType}; expected VT_DISPATCH or VT_UNKNOWN");
        }

        // VT_DISPATCH and VT_UNKNOWN store a raw interface pointer in the variant union.
        // DispatchObject's nint constructor AddRefs, so the pointer survives the caller's
        // VariantClear on the source variant.
        var pUnk = variant.GetRawDataRef<nint>();
        if (pUnk == 0)
        {
            throw new InvalidOperationException($"'{name}' returned a null dispatch object");
        }
        return new DispatchObject(pUnk);
    }

    #endregion

    #region Core IDispatch calls via vtable

    private int GetDispId(string name)
    {
        if (_dispIdCache.TryGetValue(name, out var cached))
            return cached;

        var dispId = GetDispIdFromCom(name);
        _dispIdCache[name] = dispId;
        return dispId;
    }

    private int GetDispIdFromCom(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint* vtable = *(nint**)_pDispatch;
        // Slot 5 = GetIDsOfNames
        var getIDsOfNames = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int, int, int*, int>)vtable[5];

        var riid = Guid.Empty;
        var namePtr = Marshal.StringToCoTaskMemUni(name);
        try
        {
            int dispId;
            int hr = getIDsOfNames(_pDispatch, &riid, &namePtr, 1, NativeMethods.LOCALE_SYSTEM_DEFAULT, &dispId);
            Marshal.ThrowExceptionForHR(hr);
            return dispId;
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    private T GetProperty<T>(string name) where T : unmanaged
    {
        var result = InvokeRawWithResult(name, NativeMethods.DISPATCH_PROPERTYGET, []);
        try
        {
            return result.As<T>();
        }
        finally
        {
            result.Dispose();
        }
    }

    private ComVariant GetPropertyVariant(string name)
    {
        return InvokeRawWithResult(name, NativeMethods.DISPATCH_PROPERTYGET, []);
    }

    private void SetProperty(string name, ComVariant value)
    {
        ComVariant[] args = [value];
        try
        {
            int namedArg = NativeMethods.DISPID_PROPERTYPUT;
            InvokeRaw(name, NativeMethods.DISPATCH_PROPERTYPUT, args, &namedArg);
        }
        finally
        {
            args[0].Dispose();
        }
    }

    private void InvokeRaw(string name, int flags, ComVariant[] args, int* namedArgs = null)
    {
        var result = InvokeCore(name, flags, args, namedArgs);
        result.Dispose();
    }

    private ComVariant InvokeRawWithResult(string name, int flags, ComVariant[] args, int* namedArgs = null)
    {
        return InvokeCore(name, flags, args, namedArgs);
    }

    private ComVariant InvokeCore(string name, int flags, ComVariant[] args, int* namedArgs = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dispId = GetDispId(name);

        nint* vtable = *(nint**)_pDispatch;
        // Slot 6 = Invoke
        var invoke = (delegate* unmanaged[Stdcall]<nint, int, Guid*, int, ushort, DISPPARAMS*, ComVariant*, nint, nint, int>)vtable[6];

        var riid = Guid.Empty;
        ComVariant result = default;

        // IDispatch.Invoke expects args in reverse order
        var reversedArgs = args.Length > 0 ? ReverseArgs(args) : [];

        fixed (ComVariant* pArgs = reversedArgs.Length > 0 ? reversedArgs : null)
        {
            var dispParams = new DISPPARAMS
            {
                rgvarg = (nint)pArgs,
                cArgs = args.Length,
                rgdispidNamedArgs = (nint)namedArgs,
                cNamedArgs = namedArgs != null ? 1 : 0
            };

            int hr = invoke(_pDispatch, dispId, &riid, NativeMethods.LOCALE_SYSTEM_DEFAULT, (ushort)flags, &dispParams, &result, 0, 0);
            Marshal.ThrowExceptionForHR(hr);
        }

        return result;
    }

    #endregion

    #region Helpers

    private static ComVariant[] ArgsToVariants(object[] args)
    {
        var variants = new ComVariant[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            variants[i] = args[i] switch
            {
                bool b => ComVariant.Create((short)(b ? -1 : 0)),
                int n => ComVariant.Create(n),
                short s => ComVariant.Create(s),
                double d => ComVariant.Create(d),
                string s => ComVariant.Create(s),
                DateTime dt => ComVariant.Create(dt.ToOADate()),
                _ => throw new ArgumentException($"Unsupported argument type: {args[i]?.GetType()}", nameof(args))
            };
        }
        return variants;
    }

    private static void DisposeVariants(ComVariant[] variants)
    {
        for (int i = 0; i < variants.Length; i++)
        {
            variants[i].Dispose();
        }
    }

    private static ComVariant[] ReverseArgs(ComVariant[] args)
    {
        var reversed = new ComVariant[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            reversed[i] = args[args.Length - 1 - i];
        }
        return reversed;
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed && _pDispatch != 0)
        {
            Marshal.Release(_pDispatch);
            _pDispatch = 0;
            _disposed = true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPPARAMS
    {
        public nint rgvarg;         // ComVariant*
        public nint rgdispidNamedArgs; // int*
        public int cArgs;
        public int cNamedArgs;
    }
}
