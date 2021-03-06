﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;

namespace Internal.TypeSystem
{
    public sealed partial class InstantiatedType : MetadataType
    {
        private MetadataType _typeDef;
        private Instantiation _instantiation;

        internal InstantiatedType(MetadataType typeDef, Instantiation instantiation)
        {
            Debug.Assert(!(typeDef is InstantiatedType));
            _typeDef = typeDef;

            Debug.Assert(instantiation.Length > 0);
            _instantiation = instantiation;

            _baseType = this; // Not yet initialized flag
        }

        private int _hashCode;

        public override int GetHashCode()
        {
            if (_hashCode == 0)
                _hashCode = _instantiation.ComputeGenericInstanceHashCode(_typeDef.GetHashCode());
            return _hashCode;
        }

        public override TypeSystemContext Context
        {
            get
            {
                return _typeDef.Context;
            }
        }

        public override Instantiation Instantiation
        {
            get
            {
                return _instantiation;
            }
        }

        private MetadataType _baseType /* = this */;

        private MetadataType InitializeBaseType()
        {
            var uninst = _typeDef.MetadataBaseType;

            return (_baseType = (uninst != null) ? (MetadataType)uninst.InstantiateSignature(_instantiation, new Instantiation()) : null);
        }

        public override DefType BaseType
        {
            get
            {
                if (_baseType == this)
                    return InitializeBaseType();
                return _baseType;
            }
        }

        public override MetadataType MetadataBaseType
        {
            get
            {
                if (_baseType == this)
                    return InitializeBaseType();
                return _baseType;
            }
        }

        protected override TypeFlags ComputeTypeFlags(TypeFlags mask)
        {
            TypeFlags flags = 0;

            if ((mask & TypeFlags.ContainsGenericVariablesComputed) != 0)
            {
                flags |= TypeFlags.ContainsGenericVariablesComputed;

                for (int i = 0; i < _instantiation.Length; i++)
                {
                    if (_instantiation[i].ContainsGenericVariables)
                    {
                        flags |= TypeFlags.ContainsGenericVariables;
                        break;
                    }
                }
            }

            if ((mask & TypeFlags.CategoryMask) != 0)
            {
                flags |= _typeDef.Category;
            }

            return flags;
        }

        public override string Name
        {
            get
            {
                return _typeDef.Name;
            }
        }

        public override IEnumerable<MethodDesc> GetMethods()
        {
            foreach (var typicalMethodDef in _typeDef.GetMethods())
            {
                yield return _typeDef.Context.GetMethodForInstantiatedType(typicalMethodDef, this);
            }
        }

        // TODO: Substitutions, generics, modopts, ...
        public override MethodDesc GetMethod(string name, MethodSignature signature)
        {
            MethodDesc typicalMethodDef = _typeDef.GetMethod(name, signature);
            if (typicalMethodDef == null)
                return null;
            return _typeDef.Context.GetMethodForInstantiatedType(typicalMethodDef, this);
        }

        public override MethodDesc GetStaticConstructor()
        {
            MethodDesc typicalCctor = _typeDef.GetStaticConstructor();
            if (typicalCctor == null)
                return null;
            return _typeDef.Context.GetMethodForInstantiatedType(typicalCctor, this);
        }

        public override IEnumerable<FieldDesc> GetFields()
        {
            foreach (var fieldDef in _typeDef.GetFields())
            {
                yield return _typeDef.Context.GetFieldForInstantiatedType(fieldDef, this);
            }
        }

        // TODO: Substitutions, generics, modopts, ...
        public override FieldDesc GetField(string name)
        {
            FieldDesc fieldDef = _typeDef.GetField(name);
            if (fieldDef == null)
                return null;
            return _typeDef.Context.GetFieldForInstantiatedType(fieldDef, this);
        }

        public override TypeDesc InstantiateSignature(Instantiation typeInstantiation, Instantiation methodInstantiation)
        {
            TypeDesc[] clone = null;

            for (int i = 0; i < _instantiation.Length; i++)
            {
                TypeDesc uninst = _instantiation[i];
                TypeDesc inst = uninst.InstantiateSignature(typeInstantiation, methodInstantiation);
                if (inst != uninst)
                {
                    if (clone == null)
                    {
                        clone = new TypeDesc[_instantiation.Length];
                        for (int j = 0; j < clone.Length; j++)
                        {
                            clone[j] = _instantiation[j];
                        }
                    }
                    clone[i] = inst;
                }
            }

            return (clone == null) ? this : _typeDef.Context.GetInstantiatedType(_typeDef, new Instantiation(clone));
        }

        /// <summary>
        /// Instantiate an array of TypeDescs over typeInstantiation and methodInstantiation
        /// </summary>
        public static T[] InstantiateTypeArray<T>(T[] uninstantiatedTypes, Instantiation typeInstantiation, Instantiation methodInstantiation) where T : TypeDesc
        {
            T[] clone = null;

            for (int i = 0; i < uninstantiatedTypes.Length; i++)
            {
                T uninst = uninstantiatedTypes[i];
                TypeDesc inst = uninst.InstantiateSignature(typeInstantiation, methodInstantiation);
                if (inst != uninst)
                {
                    if (clone == null)
                    {
                        clone = new T[uninstantiatedTypes.Length];
                        for (int j = 0; j < clone.Length; j++)
                        {
                            clone[j] = uninstantiatedTypes[j];
                        }
                    }
                    clone[i] = (T)inst;
                }
            }

            return clone != null ? clone : uninstantiatedTypes;
        }

        // Strips instantiation. E.g C<int> -> C<T>
        public override TypeDesc GetTypeDefinition()
        {
            return _typeDef;
        }

        public override string ToString()
        {
            var sb = new StringBuilder(_typeDef.ToString());
            sb.Append('<');
            for (int i = 0; i < _instantiation.Length; i++)
                sb.Append(_instantiation[i].ToString());
            sb.Append('>');
            return sb.ToString();
        }

        // Properties that are passed through from the type definition
        public override ClassLayoutMetadata GetClassLayout()
        {
            return _typeDef.GetClassLayout();
        }

        public override bool IsExplicitLayout
        {
            get
            {
                return _typeDef.IsExplicitLayout;
            }
        }

        public override bool IsSequentialLayout
        {
            get
            {
                return _typeDef.IsSequentialLayout;
            }
        }

        public override bool IsBeforeFieldInit
        {
            get
            {
                return _typeDef.IsBeforeFieldInit;
            }
        }

        public override bool IsSealed
        {
            get
            {
                return _typeDef.IsSealed;
            }
        }

        public override bool IsModuleType
        {
            get
            {
                return false;
            }
        }

        public override bool HasCustomAttribute(string attributeNamespace, string attributeName)
        {
            return _typeDef.HasCustomAttribute(attributeNamespace, attributeName);
        }
    }
}
