﻿using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hosihikari.VanillaScript.Loader;
using Hosihikari.VanillaScript.QuickJS.Helper;
using Hosihikari.VanillaScript.QuickJS.Types;
using static Hosihikari.VanillaScript.QuickJS.Types.JsClassDef;

namespace Hosihikari.VanillaScript.QuickJS.Wrapper;

public class JsContextWrapper
{
    public unsafe JsContext* Context { get; }
    private readonly List<GCHandle> _savedObject = new();
    public event Action? FreeContextCallback;
    public JsRuntimeWrapper Runtime
    {
        get
        {
            unsafe
            {
                return JsRuntimeWrapper.FetchOrCreate(Native.JS_GetRuntime(Context));
            }
        }
    }

    internal void Pin(object obj)
    {
        _savedObject.Add(GCHandle.Alloc(obj));
    }

    public static unsafe implicit operator JsContextWrapper(JsContext* ctx)
    {
        return FetchOrCreate(ctx);
    }

    private unsafe JsContextWrapper(JsContext* ctx)
    {
        Context = ctx;
        Manager.SetupContext(this);
    }

    public static bool TryGet(nint ctxPtr, [NotNullWhen(true)] out JsContextWrapper? ctx)
    {
        unsafe
        {
            if (
                Manager.LoadedScriptsContext.FirstOrDefault(x => x.Context == ctxPtr.ToPointer()) is
                { } oldCtx
            )
            {
                ctx = oldCtx;
                return true;
            }

            ctx = null;
            return false;
        }
    }

    public static unsafe JsContextWrapper FetchOrCreate(JsContext* ctx)
    {
        if (Manager.LoadedScriptsContext.FirstOrDefault(x => x.Context == ctx) is { } oldCtx)
        {
            return oldCtx;
        }
        var newInstance = new JsContextWrapper(ctx);
        Manager.LoadedScriptsContext.Add(newInstance);
        return newInstance;
    }

    private bool _freed = false;

    internal void ThrowIfFree()
    {
        if (_freed)
        {
            throw new ObjectDisposedException("JsContextWrapper");
        }
    }

    internal void FreeValues()
    {
        FreeContextCallback?.Invoke();
    }

    internal void Free()
    {
        try
        {
            foreach (var pinedItem in _savedObject)
            {
                pinedItem.Free();
            }
        }
        finally
        {
            _freed = true;
        }
    }

    public AutoDropJsValue EvalScript(
        string relativePath,
        string bytes,
        JsEvalFlag flags = JsEvalFlag.TypeModule
    )
    {
        unsafe
        {
            return Native.JS_Eval(Context, relativePath, bytes, flags);
        }
    }

    public AutoDropJsValue GetGlobalObject()
    {
        unsafe
        {
            return Native.JS_GetGlobalObject(Context);
        }
    }

    public unsafe delegate int NativeJsModuleInitFunc(JsContext* ctx, JsModuleDef* module);

    //key moduleName, value callback on import
    private readonly Dictionary<string, Dictionary<string, JsModuleImportDelegate>> _moduleImport =
        new();

    public JsModuleDefWrapper NewModule(string moduleName)
    {
        unsafe
        {
            if (_moduleImport.ContainsKey(moduleName))
            {
                throw new ArgumentException($"module {moduleName} already exists in this context");
            }
            var membersList = new Dictionary<string, JsModuleImportDelegate>();
            _moduleImport.Add(moduleName, membersList);
            var moduleInitFunc = new NativeJsModuleInitFunc(
                (ctx, def) =>
                {
                    var ctxInstance = FetchOrCreate(ctx);

                    foreach (var (name, callback) in membersList)
                    {
                        try
                        {
                            var instance = callback(ctxInstance); //get real JsValue form callback (such lazy load)
                            Native.JS_SetModuleExport(ctx, def, name, instance);
                        }
                        catch (Exception ex)
                        {
                            Log.Logger.Error(
                                $"'import {{ {name} }} from {moduleName}' failed.",
                                ex
                            );
                            return 1;
                        }
                    }
                    return 0;
                }
            );
            var module = Native.JS_NewCModule(
                Context,
                moduleName,
                (delegate* unmanaged<JsContext*, JsModuleDef*, int>)
                    Marshal.GetFunctionPointerForDelegate(moduleInitFunc).ToPointer() // callback when import this module in js
            );
            Pin(moduleInitFunc);
            return new JsModuleDefWrapper
            {
                Context = this,
                ModuleDef = module,
                Members = membersList,
                ModuleName = moduleName
            };
        }
    }

    public AutoDropJsValue NewObject()
    {
        unsafe
        {
            return JsValueCreateHelper.NewObject(Context);
        }
    }

    //public AutoDropJsValue NewObject(JsClassId classId, nint opaque)
    //{
    //    unsafe
    //    {
    //        var result = JsValueCreateHelper.NewObject(Context, classId);
    //        Native.JS_SetOpaque(result.Value, opaque);
    //        return result;
    //    }
    //}

    public AutoDropJsValue NewString(string str)
    {
        unsafe
        {
            return JsValueCreateHelper.NewString(Context, str);
        }
    }

    public AutoDropJsValue NewObjectFromJson(string json)
    {
        unsafe
        {
            return JsValueCreateHelper.FromJson(Context, json);
        }
    }

    public unsafe delegate JsValue JsCFunctionDelegate(
        JsContext* ctx,
        JsValue val,
        int argCount,
        JsValue* argvIn
    );

    /// <summary>
    /// memory safe
    /// will auto free when related JsValue free
    /// </summary>
    /// <param name="func"></param>
    /// <returns></returns>
    public AutoDropJsValue NewJsFunctionObject(JsNativeFunctionDelegate func)
    {
        return NewFunctionObject(new ClrFunctionProxyInstance(func));
    }

    /// <summary>
    /// Note: at least 'length' arguments will be readable in 'argv'
    /// Dot not call frequently, it will be free only when context free,
    ///     only use this for module init function that only called once each context.
    ///     so it would be better to use <see cref="NewStaticJsFunction"/> instead if used for some callback.
    ///     or use <see cref="NewJsFunctionObject"/> for dynamic call, it could be free automatically when no longer use in js.
    /// </summary>
    public AutoDropJsValue NewJsFunction(
        string name,
        int argumentLength,
        JsNativeFunctionDelegate func,
        JsCFunctionEnum protoType = JsCFunctionEnum.Generic
    )
    {
        unsafe
        {
            var funcInstance = new JsCFunctionDelegate(
                (ctx, val, argCount, argvIn) =>
                {
                    try
                    {
                        return func(this, val, new ReadOnlySpan<JsValue>(argvIn, argCount));
                    }
                    catch (Exception ex)
                    { //pass the exception to js context
                        Native.JS_ThrowInternalError(ctx, ex);
                        return JsValueCreateHelper.Exception;
                    }
                }
            );
            var result = Native.JS_NewCFunction2(
                Context,
                (delegate* unmanaged<JsContext*, JsValue, int, JsValue*, JsValue>)
                    Marshal.GetFunctionPointerForDelegate(funcInstance).ToPointer(),
                name,
                argumentLength,
                protoType,
                0
            );
            Pin(funcInstance); //prevent gc
            return result;
        }
    }

    /// <summary>
    /// don't forget to process error and throw it to js context
    /// use like:
    /// <code>
    ///     Native.JS_ThrowInternalError(ctx, ex);
    ///     return JsValueCreateHelper.Exception;
    /// </code>
    /// </summary>
    /// <param name="name"></param>
    /// <param name="argumentLength"></param>
    /// <param name="protoType"></param>
    /// <param name="func"></param>
    /// <returns></returns>
    public unsafe AutoDropJsValue NewStaticJsFunction(
        string name,
        int argumentLength,
        delegate* unmanaged<JsContext*, JsValue, int, JsValue*, JsValue> func,
        JsCFunctionEnum protoType = JsCFunctionEnum.Generic
    )
    {
        return Native.JS_NewCFunction2(Context, func, name, argumentLength, protoType, 0);
    }

    public AutoDropJsValue NewObject<T>(T data)
        where T : ClrProxyBase
    {
        unsafe
        {
            if (!_typeClassIdCache.TryGetValue(typeof(ClrProxyBase), out var classId))
                classId = RegisterClrObjectClassInternal(); //register class if not exists
            var result = JsValueCreateHelper.NewObject(Context, classId);
            var opaque = GCHandle.ToIntPtr(GCHandle.Alloc(data));
            Native.JS_SetOpaque(result.Value, opaque);
            return result;
        }
    }

    public AutoDropJsValue NewFunctionObject<T>(T data)
        where T : ClrFunctionProxyBase
    {
        unsafe
        {
            if (!_typeClassIdCache.TryGetValue(typeof(ClrFunctionProxyBase), out var classId))
                classId = RegisterClrFunctionClassInternal(); //register class if not exists
            var result = JsValueCreateHelper.NewObject(Context, classId);
            var opaque = GCHandle.ToIntPtr(GCHandle.Alloc(data));
            Native.JS_SetOpaque(result.Value, opaque);
            return result;
        }
    }

    private readonly Dictionary<Type, JsClassId> _typeClassIdCache = new();

    #region AutoRegister
    private JsClassId RegisterClrObjectClassInternal()
    {
        unsafe
        {
            return RegisterClass<ClrProxyBase>(
                "ClrObjectProxy",
                new JsClassDef
                {
                    Call = &ClrFunctionProxyBase.JsClassCall,
                    Finalizer = &ClrFunctionProxyBase.JsClassFinalizer,
                    GcMark = &ClrProxyBase.JsClassGcMark,
                    Exotic = (JsClassExoticMethods*)
                        ClrProxyBase.JsClassExoticMethods.Value.ToPointer()
                }
            );
        }
    }

    private JsClassId RegisterClrFunctionClassInternal()
    {
        unsafe
        {
            return RegisterClass<ClrFunctionProxyBase>(
                "ClrFunctionProxy",
                new JsClassDef
                {
                    Call = &ClrFunctionProxyBase.JsClassCall,
                    Finalizer = &ClrFunctionProxyBase.JsClassFinalizer,
                    GcMark = &ClrProxyBase.JsClassGcMark,
                }
            );
        }
    }

    private JsClassId RegisterClass<T>(string name, JsClassDef classDef)
        where T : ClrFunctionProxyBase
    {
        Runtime.TryGetClassId(name, out var id); //get old id
        var item = Runtime.NewRegisterClass(name, classDef, id);
        _typeClassIdCache[typeof(T)] = item;
        return item;
    }
    #endregion

    public AutoDropJsValue NewObject(JsClassId classId, nint opaque)
    {
        unsafe
        {
            var instance = Native.JS_NewObjectClass(Context, classId);
            Native.JS_SetOpaque(instance.Value, opaque);
            return instance;
        }
    }

    public JsValue ThrowJsError(Exception exception)
    {
        unsafe
        {
            return Native.JS_ThrowInternalError(Context, exception);
        }
    }

    public AutoDropJsAtom NewAtom(string name)
    {
        unsafe
        {
            return Native.JS_NewAtom(Context, name);
        }
    }

    public AutoDropJsValue NewArray<T>(IEnumerable<T> data)
        where T : unmanaged
    {
        return NewArray(data.ToArray()); //todo : optimize use js iterator
    }

    public AutoDropJsValue NewArray(IEnumerable<string> data)
    {
        return NewArray(data.ToArray()); //todo : optimize use js iterator
    }

    public AutoDropJsValue NewArray<T>(T[] data)
        where T : unmanaged
    {
        unsafe
        {
            return JsValueCreateHelper.NewArray<T>(Context, data);
        }
    }

    public AutoDropJsValue NewArray(string[] data)
    {
        unsafe
        {
            return JsValueCreateHelper.NewArray(Context, data);
        }
    }
}
