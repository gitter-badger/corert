﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

using Internal.IL;
using Internal.IL.Stubs;

using Internal.JitInterface;
using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysisFramework;

namespace ILCompiler
{
    public struct CompilationOptions
    {
        public bool IsCppCodeGen;
        public bool NoLineNumbers;
        public string DgmlLog;
        public bool FullLog;
    }

    public partial class Compilation
    {
        private readonly CompilerTypeSystemContext _typeSystemContext;
        private readonly CompilationOptions _options;

        private NodeFactory _nodeFactory;
        private DependencyAnalyzerBase<NodeFactory> _dependencyGraph;

        private Dictionary<TypeDesc, RegisteredType> _registeredTypes = new Dictionary<TypeDesc, RegisteredType>();
        private Dictionary<MethodDesc, RegisteredMethod> _registeredMethods = new Dictionary<MethodDesc, RegisteredMethod>();
        private Dictionary<FieldDesc, RegisteredField> _registeredFields = new Dictionary<FieldDesc, RegisteredField>();
        private List<MethodDesc> _methodsThatNeedsCompilation = null;

        private NameMangler _nameMangler = null;

        private ILCompiler.CppCodeGen.CppWriter _cppWriter = null;

        public Compilation(CompilerTypeSystemContext typeSystemContext, CompilationOptions options)
        {
            _typeSystemContext = typeSystemContext;
            _options = options;

            _nameMangler = new NameMangler(this);
        }

        public CompilerTypeSystemContext TypeSystemContext
        {
            get
            {
                return _typeSystemContext;
            }
        }

        public NameMangler NameMangler
        {
            get
            {
                return _nameMangler;
            }
        }

        public TextWriter Log
        {
            get;
            set;
        }

        public string OutputPath
        {
            get;
            set;
        }

        public TextWriter Out
        {
            get;
            set;
        }

        private MethodDesc _mainMethod;

        internal MethodDesc MainMethod
        {
            get
            {
                return _mainMethod;
            }
        }

        internal bool IsCppCodeGen
        {
            get
            {
                return _options.IsCppCodeGen;
            }
        }

        internal CompilationOptions Options
        {
            get
            {
                return _options;
            }
        }

        internal IEnumerable<RegisteredType> RegisteredTypes
        {
            get
            {
                return _registeredTypes.Values;
            }
        }

        internal RegisteredType GetRegisteredType(TypeDesc type)
        {
            RegisteredType existingRegistration;
            if (_registeredTypes.TryGetValue(type, out existingRegistration))
                return existingRegistration;

            RegisteredType registration = new RegisteredType() { Type = type };
            _registeredTypes.Add(type, registration);

            // Register all base types too
            var baseType = type.BaseType;
            if (baseType != null)
                GetRegisteredType(baseType);

            return registration;
        }

        internal RegisteredMethod GetRegisteredMethod(MethodDesc method)
        {
            RegisteredMethod existingRegistration;
            if (_registeredMethods.TryGetValue(method, out existingRegistration))
                return existingRegistration;

            RegisteredMethod registration = new RegisteredMethod() { Method = method };
            _registeredMethods.Add(method, registration);

            GetRegisteredType(method.OwningType);

            return registration;
        }

        internal RegisteredField GetRegisteredField(FieldDesc field)
        {
            RegisteredField existingRegistration;
            if (_registeredFields.TryGetValue(field, out existingRegistration))
                return existingRegistration;

            RegisteredField registration = new RegisteredField() { Field = field };
            _registeredFields.Add(field, registration);

            GetRegisteredType(field.OwningType);

            return registration;
        }

        private ILProvider _ilProvider = new ILProvider();

        public MethodIL GetMethodIL(MethodDesc method)
        {
            return _ilProvider.GetMethodIL(method);
        }

        private void CompileMethods()
        {
            var pendingMethods = _methodsThatNeedsCompilation;
            _methodsThatNeedsCompilation = null;

            foreach (MethodDesc method in pendingMethods)
            {
                _cppWriter.CompileMethod(method);
            }
        }

        private void ExpandVirtualMethods()
        {
            // Take a snapshot of _registeredTypes - new registered types can be added during the expansion
            foreach (var reg in _registeredTypes.Values.ToArray())
            {
                if (!reg.Constructed)
                    continue;

                TypeDesc declType = reg.Type;
                while (declType != null)
                {
                    var declReg = GetRegisteredType(declType);
                    if (declReg.VirtualSlots != null)
                    {
                        for (int i = 0; i < declReg.VirtualSlots.Count; i++)
                        {
                            MethodDesc declMethod = declReg.VirtualSlots[i];

                            AddMethod(VirtualFunctionResolution.FindVirtualFunctionTargetMethodOnObjectType(declMethod, reg.Type.GetClosestMetadataType()));
                        }
                    }

                    declType = declType.BaseType;
                }
            }
        }

        private CorInfoImpl _corInfo;

        public void CompileSingleFile(MethodDesc mainMethod)
        {
            if (_options.IsCppCodeGen)
            {
                _cppWriter = new CppCodeGen.CppWriter(this);
            }
            else
            {
                _corInfo = new CorInfoImpl(this);
            }

            _mainMethod = mainMethod;

            if (!_options.IsCppCodeGen)
            {
                NodeFactory.NameMangler = NameMangler;

                _nodeFactory = new NodeFactory(_typeSystemContext);

                // Choose which dependency graph implementation to use based on the amount of logging requested.
                if (_options.DgmlLog == null)
                {
                    // No log uses the NoLogStrategy
                    _dependencyGraph = new DependencyAnalyzer<NoLogStrategy<NodeFactory>, NodeFactory>(_nodeFactory, null);
                }
                else
                {
                    if (_options.FullLog)
                    {
                        // Full log uses the full log strategy
                        _dependencyGraph = new DependencyAnalyzer<FullGraphLogStrategy<NodeFactory>, NodeFactory>(_nodeFactory, null);
                    }
                    else
                    {
                        // Otherwise, use the first mark strategy
                        _dependencyGraph = new DependencyAnalyzer<FirstMarkLogStrategy<NodeFactory>, NodeFactory>(_nodeFactory, null);
                    }
                }

                _nodeFactory.AttachToDependencyGraph(_dependencyGraph);

                AddWellKnownTypes();
                AddCompilationRoots();

                _dependencyGraph.ComputeDependencyRoutine += ComputeDependencyNodeDependencies;
                var nodes = _dependencyGraph.MarkedNodeList;

                ObjectWriter.EmitObject(OutputPath, nodes, _nodeFactory);

                if (_options.DgmlLog != null)
                {
                    using (FileStream dgmlOutput = new FileStream(_options.DgmlLog, FileMode.Create))
                    {
                        DgmlWriter.WriteDependencyGraphToStream(dgmlOutput, _dependencyGraph);
                        dgmlOutput.Flush();
                    }
                }
            }
            else
            {
                AddWellKnownTypes();
                AddCompilationRoots();

                while (_methodsThatNeedsCompilation != null)
                {
                    CompileMethods();

                    ExpandVirtualMethods();
                }

                _cppWriter.OutputCode();
            }
        }

        private void AddCompilationRoots()
        {
            if (_mainMethod != null)
            {
                AddCompilationRoot(_mainMethod, "Main method", "__managed__Main");
            }

            foreach (var inputFile in _typeSystemContext.InputFilePaths)
            {
                var module = _typeSystemContext.GetModuleFromPath(inputFile.Value);
                foreach (var type in module.GetAllTypes())
                {
                    foreach (var method in type.GetMethods())
                    {
                        if (method.HasCustomAttribute("System.Runtime", "RuntimeExportAttribute"))
                        {
                            string exportName = ((EcmaMethod)method).GetAttributeStringValue("System.Runtime", "RuntimeExportAttribute");
                            AddCompilationRoot(method, "Runtime export", exportName);
                        }
                    }
                }
            }
        }

        private void AddCompilationRoot(MethodDesc method, string reason, string exportName = null)
        {
            if (_dependencyGraph != null)
            {
                var methodEntryPoint = _nodeFactory.MethodEntrypoint(method);

                _dependencyGraph.AddRoot(methodEntryPoint, reason);

                if (exportName != null)
                    _nodeFactory.NodeAliases.Add(methodEntryPoint, exportName);
            }
            else
            {
                AddMethod(method);
            }
        }

        private struct TypeAndMethod
        {
            public string TypeName;
            public string MethodName;
            public TypeAndMethod(string typeName, string methodName)
            {
                TypeName = typeName;
                MethodName = methodName;
            }
        }

        // List of methods that are known to throw an exception during compilation.
        // On Windows it's fine to throw it because we have a catchall block.
        // On Linux, throwing a managed exception to native code will bring down the process.
        // https://github.com/dotnet/corert/issues/162
        private HashSet<TypeAndMethod> _skipJitList = new HashSet<TypeAndMethod>
        {
            new TypeAndMethod("System.SR", "GetResourceString"),
            new TypeAndMethod("System.Text.StringBuilder", "AppendFormatHelper"),
            new TypeAndMethod("System.Collections.Concurrent.ConcurrentUnifier`2", "GetOrAdd"),
            new TypeAndMethod("System.Globalization.NumberFormatInfo", "GetInstance"),
            new TypeAndMethod("System.Collections.Concurrent.ConcurrentUnifierW`2", "GetOrAdd"),
            new TypeAndMethod("System.Collections.Generic.LowLevelDictionary`2", "Find"),
            new TypeAndMethod("System.Collections.Generic.LowLevelDictionary`2", "GetBucket"),
            new TypeAndMethod("System.Collections.Generic.ArraySortHelper`1", "InternalBinarySearch"),
            new TypeAndMethod("System.RuntimeExceptionHelpers", "SerializeExceptionsForDump"),
            new TypeAndMethod("System.InvokeUtils", "CheckArgument"),
            new TypeAndMethod("System.Runtime.InteropServices.ExceptionHelpers", "GetMappingExceptionForHR"),
        };

        private void ComputeDependencyNodeDependencies(List<DependencyNodeCore<NodeFactory>> obj)
        {
            foreach (MethodCodeNode methodCodeNodeNeedingCode in obj)
            {
                MethodDesc method = methodCodeNodeNeedingCode.Method;
                string methodName = method.ToString();
                Log.WriteLine("Compiling " + methodName);

                var methodIL = _ilProvider.GetMethodIL(method);
                if (methodIL == null)
                    return;

                MethodCode methodCode;
                try
                {
                    if (_skipJitList.Contains(new TypeAndMethod(method.OwningType.Name, method.Name)))
                    {
                        throw new NotImplementedException("SkipJIT");
                    }

                    methodCode = _corInfo.CompileMethod(method);

                    if (methodCode.Relocs != null)
                    {
                        if (methodCode.Relocs.Any(r => r.Target is FieldDesc))
                        {
                            // We only support FieldDesc for InitializeArray intrinsic right now.
                            throw new NotImplementedException("RuntimeFieldHandle is not implemented");
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.WriteLine("*** " + e.Message + " (" + method + ")");

                    // Call the __not_yet_implemented method
                    DependencyAnalysis.X64.X64Emitter emit = new DependencyAnalysis.X64.X64Emitter(_nodeFactory);
                    emit.Builder.RequireAlignment(_nodeFactory.Target.MinimumFunctionAlignment);
                    emit.Builder.DefinedSymbols.Add(methodCodeNodeNeedingCode);

                    emit.EmitLEAQ(emit.TargetRegister.Arg0, _nodeFactory.StringIndirection(method.ToString()));
                    DependencyAnalysis.X64.AddrMode loadFromArg0 =
                        new DependencyAnalysis.X64.AddrMode(emit.TargetRegister.Arg0, null, 0, 0, DependencyAnalysis.X64.AddrModeSize.Int64);
                    emit.EmitMOV(emit.TargetRegister.Arg0, ref loadFromArg0);
                    emit.EmitMOV(emit.TargetRegister.Arg0, ref loadFromArg0);

                    emit.EmitLEAQ(emit.TargetRegister.Arg1, _nodeFactory.StringIndirection(e.Message));
                    DependencyAnalysis.X64.AddrMode loadFromArg1 =
                        new DependencyAnalysis.X64.AddrMode(emit.TargetRegister.Arg1, null, 0, 0, DependencyAnalysis.X64.AddrModeSize.Int64);
                    emit.EmitMOV(emit.TargetRegister.Arg1, ref loadFromArg1);
                    emit.EmitMOV(emit.TargetRegister.Arg1, ref loadFromArg1);

                    emit.EmitJMP(_nodeFactory.ExternSymbol("__not_yet_implemented"));
                    methodCodeNodeNeedingCode.SetCode(emit.Builder.ToObjectData());
                    continue;
                }

                ObjectDataBuilder objData = new ObjectDataBuilder();
                objData.Alignment = _nodeFactory.Target.MinimumFunctionAlignment;
                objData.EmitBytes(methodCode.Code);
                objData.DefinedSymbols.Add(methodCodeNodeNeedingCode);

                BlobNode readOnlyDataBlob = null;
                if (methodCode.ROData != null)
                {
                    readOnlyDataBlob = _nodeFactory.ReadOnlyDataBlob(
                        "__readonlydata_" + _nameMangler.GetMangledMethodName(method),
                        methodCode.ROData, methodCode.RODataAlignment);
                }

                if (methodCode.Relocs != null)
                {
                    for (int i = 0; i < methodCode.Relocs.Length; i++)
                    {
                        // TODO: Arbitrary relocs
                        if (methodCode.Relocs[i].Block != BlockType.Code)
                            throw new NotImplementedException();

                        int offset = methodCode.Relocs[i].Offset;
                        int delta = methodCode.Relocs[i].Delta;
                        RelocType relocType = (RelocType)methodCode.Relocs[i].RelocType;
                        ISymbolNode targetNode;

                        object target = methodCode.Relocs[i].Target;
                        if (target is MethodDesc)
                        {
                            targetNode = _nodeFactory.MethodEntrypoint((MethodDesc)target);
                        }
                        else if (target is ReadyToRunHelper)
                        {
                            targetNode = _nodeFactory.ReadyToRunHelper((ReadyToRunHelper)target);
                        }
                        else if (target is JitHelper)
                        {
                            targetNode = _nodeFactory.ExternSymbol(((JitHelper)target).MangledName);
                        }
                        else if (target is string)
                        {
                            targetNode = _nodeFactory.StringIndirection((string)target);
                        }
                        else if (target is TypeDesc)
                        {
                            targetNode = _nodeFactory.NecessaryTypeSymbol((TypeDesc)target);
                        }
                        else if (target is RvaFieldData)
                        {
                            var rvaFieldData = (RvaFieldData)target;
                            targetNode = _nodeFactory.ReadOnlyDataBlob(rvaFieldData.MangledName,
                                rvaFieldData.Data, _typeSystemContext.Target.PointerSize);
                        }
                        else if (target is BlockRelativeTarget)
                        {
                            var blockRelativeTarget = (BlockRelativeTarget)target;
                            // TODO: Arbitrary block relative relocs
                            if (blockRelativeTarget.Block != BlockType.ROData)
                                throw new NotImplementedException();
                            targetNode = readOnlyDataBlob;
                        }
                        else
                        {
                            // TODO:
                            throw new NotImplementedException();
                        }

                        objData.AddRelocAtOffset(targetNode, relocType, offset, delta);
                    }
                }
                // TODO: ColdCode
                if (methodCode.ColdCode != null)
                    throw new NotImplementedException();

                methodCodeNodeNeedingCode.SetCode(objData.ToObjectData());

                methodCodeNodeNeedingCode.InitializeFrameInfos(methodCode.FrameInfos);
                methodCodeNodeNeedingCode.InitializeDebugLocInfos(methodCode.DebugLocInfos);
            }
        }

        private void AddWellKnownTypes()
        {
            var stringType = TypeSystemContext.GetWellKnownType(WellKnownType.String);

            if (_dependencyGraph != null)
            {
                _dependencyGraph.AddRoot(_nodeFactory.ConstructedTypeSymbol(stringType), "String type is always generated");
            }
            else
            {
                AddType(stringType);
                MarkAsConstructed(stringType);
            }
        }

        public void AddMethod(MethodDesc method)
        {
            RegisteredMethod reg = GetRegisteredMethod(method);
            if (reg.IncludedInCompilation)
                return;
            reg.IncludedInCompilation = true;

            RegisteredType regType = GetRegisteredType(method.OwningType);
            if (regType.Methods == null)
                regType.Methods = new List<RegisteredMethod>();
            regType.Methods.Add(reg);

            if (_methodsThatNeedsCompilation == null)
                _methodsThatNeedsCompilation = new List<MethodDesc>();
            _methodsThatNeedsCompilation.Add(method);

            if (_options.IsCppCodeGen)
            {
                // Precreate name to ensure that all types referenced by signatures are present
                GetRegisteredType(method.OwningType);
                var signature = method.Signature;
                GetRegisteredType(signature.ReturnType);
                for (int i = 0; i < signature.Length; i++)
                    GetRegisteredType(signature[i]);
            }
        }

        public void AddVirtualSlot(MethodDesc method)
        {
            RegisteredType reg = GetRegisteredType(method.OwningType);

            if (reg.VirtualSlots == null)
                reg.VirtualSlots = new List<MethodDesc>();

            for (int i = 0; i < reg.VirtualSlots.Count; i++)
            {
                if (reg.VirtualSlots[i] == method)
                    return;
            }

            reg.VirtualSlots.Add(method);
        }

        public void MarkAsConstructed(TypeDesc type)
        {
            GetRegisteredType(type).Constructed = true;
        }

        public void AddType(TypeDesc type)
        {
            RegisteredType reg = GetRegisteredType(type);
            if (reg.IncludedInCompilation)
                return;
            reg.IncludedInCompilation = true;

            TypeDesc baseType = type.BaseType;
            if (baseType != null)
                AddType(baseType);
            if (type.IsArray)
                AddType(((ArrayType)type).ElementType);
        }

        public void AddField(FieldDesc field)
        {
            RegisteredField reg = GetRegisteredField(field);
            if (reg.IncludedInCompilation)
                return;
            reg.IncludedInCompilation = true;

            if (_options.IsCppCodeGen)
            {
                // Precreate name to ensure that all types referenced by signatures are present
                GetRegisteredType(field.OwningType);
                GetRegisteredType(field.FieldType);
            }
        }

        private struct ReadyToRunHelperKey : IEquatable<ReadyToRunHelperKey>
        {
            private ReadyToRunHelperId _id;
            private Object _obj;

            public ReadyToRunHelperKey(ReadyToRunHelperId id, Object obj)
            {
                _id = id;
                _obj = obj;
            }

            public bool Equals(ReadyToRunHelperKey other)
            {
                return (_id == other._id) && ReferenceEquals(_obj, other._obj);
            }

            public override int GetHashCode()
            {
                return _id.GetHashCode() ^ _obj.GetHashCode();
            }

            public override bool Equals(object obj)
            {
                if (!(obj is ReadyToRunHelperKey))
                    return false;

                return Equals((ReadyToRunHelperKey)obj);
            }
        }

        private Dictionary<ReadyToRunHelperKey, ReadyToRunHelper> _readyToRunHelpers = new Dictionary<ReadyToRunHelperKey, ReadyToRunHelper>();

        public Object GetReadyToRunHelper(ReadyToRunHelperId id, Object target)
        {
            ReadyToRunHelper helper;

            ReadyToRunHelperKey key = new ReadyToRunHelperKey(id, target);
            if (!_readyToRunHelpers.TryGetValue(key, out helper))
                _readyToRunHelpers.Add(key, helper = new ReadyToRunHelper(this, id, target));

            return helper;
        }

        private Dictionary<JitHelperId, JitHelper> _jitHelpers = new Dictionary<JitHelperId, JitHelper>();
        public Object GetJitHelper(JitHelperId id)
        {
            JitHelper helper;

            if (!_jitHelpers.TryGetValue(id, out helper))
                _jitHelpers.Add(id, helper = new JitHelper(this, id));

            return helper;
        }

        private Dictionary<MethodDesc, DelegateInfo> _delegateInfos = new Dictionary<MethodDesc, DelegateInfo>();
        public DelegateInfo GetDelegateCtor(MethodDesc target)
        {
            DelegateInfo info;

            if (!_delegateInfos.TryGetValue(target, out info))
            {
                _delegateInfos.Add(target, info = new DelegateInfo(this, target));
            }

            return info;
        }

        private Dictionary<FieldDesc, RvaFieldData> _rvaFieldDatas = new Dictionary<FieldDesc, RvaFieldData>();

        /// <summary>
        /// Gets an object representing the static data for RVA mapped fields from the PE image.
        /// </summary>
        public object GetFieldRvaData(FieldDesc field)
        {
            RvaFieldData result;
            if (!_rvaFieldDatas.TryGetValue(field, out result))
            {
                _rvaFieldDatas.Add(field, result = new RvaFieldData(this, field));
            }
            return result;
        }
    }
}
