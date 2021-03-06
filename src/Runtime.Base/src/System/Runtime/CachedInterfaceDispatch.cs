// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Runtime;

namespace System.Runtime
{
    internal unsafe static class CachedInterfaceDispatch
    {
        [RuntimeExport("RhpCidResolve")]
        private static IntPtr RhpCidResolve(object pObject, IntPtr pCell)
        {
            try
            {
                EEType* pInterfaceType;
                ushort slot;
                InternalCalls.RhpGetDispatchCellInfo(pCell, &pInterfaceType, &slot);
                IntPtr pTargetCode = RhResolveDispatchWorker(pObject, pInterfaceType, slot);
                if (pTargetCode != IntPtr.Zero)
                {
                    return InternalCalls.RhpUpdateDispatchCellCache(pCell, pTargetCode, pObject.EEType);
                }
            }
            catch
            {
                // Exceptions are not permitted to escape from runtime->managed callbacks
                EH.FailFast(RhFailFastReason.InternalError, null);
            }

            // "Valid method implementation was not found."
            EH.FailFast(RhFailFastReason.InternalError, null);
            return IntPtr.Zero;
        }

        [RuntimeExport("RhpResolveInterfaceMethod")]
        private static IntPtr RhpResolveInterfaceMethod(object pObject, IntPtr pCell)
        {
            if (pObject == null)
            {
                // ProjectN Optimizer may perform code motion on dispatch such that it occurs independant of 
                // null check on "this" pointer. Allow for this case by returning back an invalid pointer.
                return IntPtr.Zero;
            }

            EEType* pInstanceType = pObject.EEType;

            // This method is used for the implementation of LOAD_VIRT_FUNCTION and in that case the mapping we want
            // may already be in the cache.
            IntPtr pTargetCode = InternalCalls.RhpSearchDispatchCellCache(pCell, pInstanceType);
            if (pTargetCode != IntPtr.Zero)
                return pTargetCode;

            // Otherwise call the version of this method that knows how to resolve the method manually.
            return RhpCidResolve(pObject, pCell);
        }

        [RuntimeExport("RhResolveDispatch")]
        private static IntPtr RhResolveDispatch(object pObject, EETypePtr interfaceType, ushort slot)
        {
            return RhResolveDispatchWorker(pObject, interfaceType.ToPointer(), slot);
        }

        [RuntimeExport("RhResolveDispatchOnType")]
        private static IntPtr RhResolveDispatchOnType(EETypePtr instanceType, EETypePtr interfaceType, ushort slot)
        {
            // Type of object we're dispatching on.
            EEType* pInstanceType = instanceType.ToPointer();

            // Type of interface
            EEType* pInterfaceType = interfaceType.ToPointer();

            return DispatchResolve.FindInterfaceMethodImplementationTarget(pInstanceType,
                                                                          pInterfaceType,
                                                                          slot);
        }

        private static IntPtr RhResolveDispatchWorker(object pObject, EEType* pInterfaceType, ushort slot)
        {
            // Type of object we're dispatching on.
            EEType* pInstanceType = pObject.EEType;

            // Type whose DispatchMap is used. Usually the same as the above but for types which implement ICastable
            // we may repeat this process with an alternate type.
            EEType* pResolvingInstanceType = pInstanceType;

            IntPtr pTargetCode = DispatchResolve.FindInterfaceMethodImplementationTarget(pResolvingInstanceType,
                                                                          pInterfaceType,
                                                                          slot);

            if (pTargetCode == IntPtr.Zero && pInstanceType->IsICastable)
            {
                // Dispatch not resolved through normal dispatch map, try using the ICastable
                IntPtr pfnGetImplTypeMethod = pInstanceType->ICastableGetImplTypeMethod;
                pResolvingInstanceType = (EEType*)CalliIntrinsics.Call<IntPtr>(pfnGetImplTypeMethod, pObject, new IntPtr(pInterfaceType));

                pTargetCode = DispatchResolve.FindInterfaceMethodImplementationTarget(pResolvingInstanceType,
                                                                         pInterfaceType,
                                                                         slot);
            }

            return pTargetCode;
        }
    }
}
