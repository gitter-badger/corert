﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using Debug = System.Diagnostics.Debug;

namespace Internal.TypeSystem
{
    /// <summary>
    /// MetadataFieldLayout algorithm which can be used to compute field layout
    /// for any MetadataType where all fields are available by calling GetFields.
    /// </summary>
    public class MetadataFieldLayoutAlgorithm : FieldLayoutAlgorithm
    {
        public override ComputedInstanceFieldLayout ComputeInstanceFieldLayout(DefType defType)
        {
            MetadataType type = (MetadataType)defType;
            // CLI - Partition 1, section 9.5 - Generic types shall not be marked explicitlayout.  
            if (type.HasInstantiation && type.IsExplicitLayout)
            {
                throw new TypeLoadException();
            }

            // Count the number of instance fields in advance for convenience
            int numInstanceFields = 0;
            foreach (var field in type.GetFields())
                if (!field.IsStatic)
                    numInstanceFields++;

            if (type.IsModuleType)
            {
                // This is a global type, it must not have instance fields.
                if (numInstanceFields > 0)
                {
                    throw new TypeLoadException();
                }

                // Global types do not do the rest of instance field layout.
                ComputedInstanceFieldLayout result = new ComputedInstanceFieldLayout();
                result.PackValue = type.Context.Target.DefaultPackingSize;
                return result;
            }

            // CLI - Partition 2, section 22.8
            // A type has layout if it is marked SequentialLayout or ExplicitLayout.  If any type within an inheritance chain has layout, 
            // then so shall all its base classes, up to the one that descends immediately from System.ValueType (if it exists in the type’s 
            // hierarchy); otherwise, from System.Object
            // Note: While the CLI isn't clearly worded, the layout needs to be the same for the entire chain.
            // If the current type isn't ValueType or System.Object and has a layout and the parent type isn't
            // ValueType or System.Object then the layout type attributes need to match
            if ((!type.IsValueType && !type.IsObject) &&
                (type.IsSequentialLayout || type.IsExplicitLayout) &&
                (!type.BaseType.IsValueType && !type.BaseType.IsObject))
            {
                MetadataType baseType = type.MetadataBaseType;

                if (type.IsSequentialLayout != baseType.IsSequentialLayout ||
                    type.IsExplicitLayout != baseType.IsExplicitLayout)
                {
                    throw new TypeLoadException();
                }
            }

            // Enum types must have a single instance field
            if (type.IsEnum && numInstanceFields != 1)
            {
                throw new TypeLoadException();
            }

            if (type.IsPrimitive)
            {
                // Primitive types are special - they may have a single field of the same type
                // as the type itself. They do not do the rest of instance field layout.
                if (numInstanceFields > 1)
                {
                    throw new TypeLoadException();
                }

                SizeAndAlignment instanceByteSizeAndAlignment;
                var sizeAndAlignment = ComputeInstanceSize(
                    type,
                    type.Context.Target.GetWellKnownTypeSize(type),
                    type.Context.Target.GetWellKnownTypeAlignment(type),
                    out instanceByteSizeAndAlignment
                    );

                ComputedInstanceFieldLayout result = new ComputedInstanceFieldLayout
                {
                    ByteCountUnaligned = instanceByteSizeAndAlignment.Size,
                    ByteCountAlignment = instanceByteSizeAndAlignment.Alignment,
                    FieldAlignment = sizeAndAlignment.Alignment,
                    FieldSize = sizeAndAlignment.Size,
                    PackValue = type.Context.Target.DefaultPackingSize
                };

                if (numInstanceFields > 0)
                {
                    FieldDesc instanceField = null;
                    foreach (FieldDesc field in type.GetFields())
                    {
                        if (!field.IsStatic)
                        {
                            Debug.Assert(instanceField == null, "Unexpected extra instance field");
                            instanceField = field;
                        }
                    }

                    Debug.Assert(instanceField != null, "Null instance field");

                    result.Offsets = new FieldAndOffset[] {
                        new FieldAndOffset(instanceField, 0)
                    };
                }

                return result;
            }

            // Verify that no ByRef types present in this type's fields
            foreach (var field in type.GetFields())
                if (field.FieldType is ByRefType)
                    throw new TypeLoadException();

            // If the type has layout, read its packing and size info
            // If the type has explicit layout, also read the field offset info
            if (type.IsExplicitLayout || type.IsSequentialLayout)
            {
                if (type.IsEnum)
                {
                    throw new TypeLoadException();
                }

                var layoutMetadata = type.GetClassLayout();

                // If packing is out of range or not a power of two, throw that the size is invalid
                int packing = layoutMetadata.PackingSize;
                if (packing < 0 || packing > 128 || ((packing & (packing - 1)) != 0))
                {
                    throw new TypeLoadException();
                }

                Debug.Assert(layoutMetadata.Offsets == null || layoutMetadata.Offsets.Length == numInstanceFields);
            }

            // At this point all special cases are handled and all inputs validated

            if (type.IsExplicitLayout)
            {
                return ComputeExplicitFieldLayout(type, numInstanceFields);
            }
            else
            {
                // Treat auto layout as sequential for now
                return ComputeSequentialFieldLayout(type, numInstanceFields);
            }
        }

        public unsafe override ComputedStaticFieldLayout ComputeStaticFieldLayout(DefType defType)
        {
            MetadataType type = (MetadataType)defType;
            int numStaticFields = 0;

            foreach (var field in type.GetFields())
            {
                if (!field.IsStatic)
                    continue;

                numStaticFields++;
            }

            ComputedStaticFieldLayout result;
            result.GcStatics = new StaticsBlock();
            result.NonGcStatics = new StaticsBlock();
            result.ThreadStatics = new StaticsBlock();

            if (numStaticFields == 0)
            {
                result.Offsets = null;
                return result;
            }

            result.Offsets = new FieldAndOffset[numStaticFields];

            PrepareRuntimeSpecificStaticFieldLayout(type.Context, ref result);

            int index = 0;

            foreach (var field in type.GetFields())
            {
                if (!field.IsStatic)
                    continue;

                StaticsBlock* block =
                    field.IsThreadStatic ? &result.ThreadStatics :
                    field.HasGCStaticBase ? &result.GcStatics :
                    &result.NonGcStatics;

                if (field.HasRva)
                {
                    throw new NotImplementedException();
                }

                SizeAndAlignment sizeAndAlignment = ComputeFieldSizeAndAlignment(field.FieldType, type.Context.Target.DefaultPackingSize);

                block->Size = AlignmentHelper.AlignUp(block->Size, sizeAndAlignment.Alignment);
                result.Offsets[index] = new FieldAndOffset(field, block->Size);
                block->Size = checked(block->Size + sizeAndAlignment.Size);

                block->LargestAlignment = Math.Max(block->LargestAlignment, sizeAndAlignment.Alignment);

                index++;
            }

            FinalizeRuntimeSpecificStaticFieldLayout(type.Context, ref result);

            return result;
        }

        public override bool ComputeContainsPointers(DefType type)
        {
            bool someFieldContainsPointers = false;

            foreach (var field in type.GetFields())
            {
                if (field.IsStatic)
                    continue;

                TypeDesc fieldType = field.FieldType;
                if (fieldType.IsValueType)
                {
                    if (fieldType.IsPrimitive)
                        continue;

                    if (((MetadataType)fieldType).ContainsPointers)
                    {
                        someFieldContainsPointers = true;
                        break;
                    }
                }
                else if (fieldType is DefType || fieldType is ArrayType || fieldType.IsByRef)
                {
                    someFieldContainsPointers = true;
                    break;
                }
            }

            return someFieldContainsPointers;
        }

        /// <summary>
        /// Called during static field layout to setup initial contents of statics blocks
        /// </summary>
        protected virtual void PrepareRuntimeSpecificStaticFieldLayout(TypeSystemContext context, ref ComputedStaticFieldLayout layout)
        {
        }

        /// <summary>
        /// Called during static field layout to finish static block layout
        /// </summary>
        protected virtual void FinalizeRuntimeSpecificStaticFieldLayout(TypeSystemContext context, ref ComputedStaticFieldLayout layout)
        {
        }

        private static ComputedInstanceFieldLayout ComputeExplicitFieldLayout(MetadataType type, int numInstanceFields)
        {
            // Instance slice size is the total size of instance not including the base type.
            // It is calculated as the field whose offset and size add to the greatest value.
            int cumulativeInstanceFieldPos =
                type.HasBaseType && !type.IsValueType ? type.BaseType.InstanceByteCount : 0;
            int instanceSize = cumulativeInstanceFieldPos;

            var layoutMetadata = type.GetClassLayout();

            int packingSize = ComputePackingSize(type);
            int largestAlignmentRequired = 1;

            var offsets = new FieldAndOffset[numInstanceFields];
            int fieldOrdinal = 0;

            foreach (var fieldAndOffset in layoutMetadata.Offsets)
            {
                var fieldSizeAndAlignment = ComputeFieldSizeAndAlignment(fieldAndOffset.Field.FieldType, packingSize);

                if (fieldSizeAndAlignment.Alignment > largestAlignmentRequired)
                    largestAlignmentRequired = fieldSizeAndAlignment.Alignment;

                if (fieldAndOffset.Offset == FieldAndOffset.InvalidOffset)
                    throw new TypeLoadException();

                int computedOffset = checked(fieldAndOffset.Offset + cumulativeInstanceFieldPos);

                switch (fieldAndOffset.Field.FieldType.Category)
                {
                    case TypeFlags.Array:
                    case TypeFlags.Class:
                        {
                            int offsetModulo = computedOffset % type.Context.Target.PointerSize;
                            if (offsetModulo != 0)
                            {
                                // GC pointers MUST be aligned.
                                if (offsetModulo == 4)
                                {
                                    // We must be attempting to compile a 32bit app targeting a 64 bit platform.
                                    throw new TypeLoadException();
                                }
                                else
                                {
                                    // Its just wrong
                                    throw new TypeLoadException();
                                }
                            }
                            break;
                        }
                }

                offsets[fieldOrdinal] = new FieldAndOffset(fieldAndOffset.Field, computedOffset);

                int fieldExtent = checked(computedOffset + fieldSizeAndAlignment.Size);
                if (fieldExtent > instanceSize)
                {
                    instanceSize = fieldExtent;
                }

                fieldOrdinal++;
            }

            if (type.IsValueType && layoutMetadata.Size > instanceSize)
            {
                instanceSize = layoutMetadata.Size;
            }

            SizeAndAlignment instanceByteSizeAndAlignment;
            var instanceSizeAndAlignment = ComputeInstanceSize(type, instanceSize, largestAlignmentRequired, out instanceByteSizeAndAlignment);

            ComputedInstanceFieldLayout computedLayout = new ComputedInstanceFieldLayout();
            computedLayout.FieldAlignment = instanceSizeAndAlignment.Alignment;
            computedLayout.FieldSize = instanceSizeAndAlignment.Size;
            computedLayout.ByteCountUnaligned = instanceByteSizeAndAlignment.Size;
            computedLayout.ByteCountAlignment = instanceByteSizeAndAlignment.Alignment;
            computedLayout.Offsets = offsets;

            return computedLayout;
        }

        private static ComputedInstanceFieldLayout ComputeSequentialFieldLayout(MetadataType type, int numInstanceFields)
        {
            var offsets = new FieldAndOffset[numInstanceFields];

            // For types inheriting from another type, field offsets continue on from where they left off
            int cumulativeInstanceFieldPos = ComputeBytesUsedInParentType(type);

            int largestAlignmentRequirement = 1;
            int fieldOrdinal = 0;
            int packingSize = ComputePackingSize(type);

            foreach (var field in type.GetFields())
            {
                if (field.IsStatic)
                    continue;

                var fieldSizeAndAlignment = ComputeFieldSizeAndAlignment(field.FieldType, packingSize);

                if (fieldSizeAndAlignment.Alignment > largestAlignmentRequirement)
                    largestAlignmentRequirement = fieldSizeAndAlignment.Alignment;

                cumulativeInstanceFieldPos = AlignmentHelper.AlignUp(cumulativeInstanceFieldPos, fieldSizeAndAlignment.Alignment);
                offsets[fieldOrdinal] = new FieldAndOffset(field, cumulativeInstanceFieldPos);
                cumulativeInstanceFieldPos = checked(cumulativeInstanceFieldPos + fieldSizeAndAlignment.Size);

                fieldOrdinal++;
            }

            if (type.IsValueType)
            {
                var layoutMetadata = type.GetClassLayout();
                cumulativeInstanceFieldPos = Math.Max(cumulativeInstanceFieldPos, layoutMetadata.Size);
            }

            SizeAndAlignment instanceByteSizeAndAlignment;
            var instanceSizeAndAlignment = ComputeInstanceSize(type, cumulativeInstanceFieldPos, largestAlignmentRequirement, out instanceByteSizeAndAlignment);

            ComputedInstanceFieldLayout computedLayout = new ComputedInstanceFieldLayout();
            computedLayout.FieldAlignment = instanceSizeAndAlignment.Alignment;
            computedLayout.FieldSize = instanceSizeAndAlignment.Size;
            computedLayout.ByteCountUnaligned = instanceByteSizeAndAlignment.Size;
            computedLayout.ByteCountAlignment = instanceByteSizeAndAlignment.Alignment;
            computedLayout.Offsets = offsets;

            return computedLayout;
        }

        private static int ComputeBytesUsedInParentType(DefType type)
        {
            int cumulativeInstanceFieldPos = 0;

            if (!type.IsValueType && type.HasBaseType)
            {
                cumulativeInstanceFieldPos = type.BaseType.InstanceByteCountUnaligned;
            }

            return cumulativeInstanceFieldPos;
        }

        private static SizeAndAlignment ComputeFieldSizeAndAlignment(TypeDesc fieldType, int packingSize)
        {
            SizeAndAlignment result;

            if (fieldType is MetadataType)
            {
                if (fieldType.IsValueType)
                {
                    MetadataType metadataType = (MetadataType)fieldType;
                    result.Size = metadataType.InstanceFieldSize;
                    result.Alignment = metadataType.InstanceFieldAlignment;
                }
                else
                {
                    result.Size = fieldType.Context.Target.PointerSize;
                    result.Alignment = fieldType.Context.Target.PointerSize;
                }
            }
            else if (fieldType is ByRefType || fieldType is ArrayType)
            {
                // This could use InstanceFieldSize/Alignment (and those results should match what's here)
                // but, its more efficient to just assume pointer size instead of fulling processing
                // the instance field layout of fieldType here.
                result.Size = fieldType.Context.Target.PointerSize;
                result.Alignment = fieldType.Context.Target.PointerSize;
            }
            else if (fieldType is PointerType)
            {
                result.Size = fieldType.Context.Target.PointerSize;
                result.Alignment = fieldType.Context.Target.PointerSize;
            }
            else
                throw new NotImplementedException();

            result.Alignment = Math.Min(result.Alignment, packingSize);

            return result;
        }

        private static int ComputePackingSize(MetadataType type)
        {
            var layoutMetadata = type.GetClassLayout();

            // If a type contains pointers then the metadata specified packing size is ignored (On desktop this is disqualification from ManagedSequential)
            if (layoutMetadata.PackingSize == 0 || type.ContainsPointers)
                return type.Context.Target.DefaultPackingSize;
            else
                return layoutMetadata.PackingSize;
        }

        private static SizeAndAlignment ComputeInstanceSize(MetadataType type, int count, int alignment, out SizeAndAlignment byteCount)
        {
            SizeAndAlignment result;

            int targetPointerSize = type.Context.Target.PointerSize;

            // Pad the length of structs to be 1 if they are empty so we have no zero-length structures
            if (type.IsValueType && count == 0)
            {
                count = 1;
            }

            if (type.IsValueType)
            {
                count = AlignmentHelper.AlignUp(count, alignment);
                result.Size = count;
                result.Alignment = alignment;
            }
            else
            {
                result.Size = targetPointerSize;
                result.Alignment = targetPointerSize;
                if (type.HasBaseType)
                    alignment = Math.Max(alignment, type.BaseType.InstanceByteAlignment);
            }

            // Determine the alignment needed by the type when allocated
            // This is target specific, and not just pointer sized due to 
            // 8 byte alignment requirements on ARM for longs and doubles
            alignment = type.Context.Target.GetObjectAlignment(alignment);

            byteCount.Size = count;
            byteCount.Alignment = alignment;

            return result;
        }

        private struct SizeAndAlignment
        {
            public int Size;
            public int Alignment;
        }
    }
}
