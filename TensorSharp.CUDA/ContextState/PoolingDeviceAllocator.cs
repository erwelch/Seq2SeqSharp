﻿using ManagedCuda;
using ManagedCuda.BasicTypes;
using System;
using System.Collections.Generic;

namespace TensorSharp.CUDA.ContextState
{
    public class PoolingDeviceAllocator : IDeviceAllocator
    {
        private const long MemoryAlignment = 256;

        private readonly CudaContext context;
        private readonly Dictionary<long, Queue<IDeviceMemory>> pools = new Dictionary<long, Queue<IDeviceMemory>>();
        //private long allocatedSize = 0;
        //private long missingCacheSize = 0;
        //private const long maxSize = (long)(1024L * 1024L * 1024L * 4L);
        private static readonly object locker = new object();

        public PoolingDeviceAllocator(CudaContext context)
        {
            this.context = context;
        }

        public void FreeMemory(bool callGC = false)
        {
            lock (locker)
            {
                if (callGC)
                {
                    GC.Collect();
                    GC.WaitForFullGCComplete();
                }

                foreach (KeyValuePair<long, Queue<IDeviceMemory>> kv in pools)
                {
                    while (kv.Value.Count > 0)
                    {
                        IDeviceMemory item = kv.Value.Dequeue();
                        if (item != null)
                        {
                            context.FreeMemory(item.Pointer);
                        }
                    }
                }
            }
        }


        public IDeviceMemory Allocate(long byteCount)
        {
            long size = PadToAlignment(byteCount, MemoryAlignment);

            lock (locker)
            {
                //   allocatedSize += size;
                if (pools.TryGetValue(size, out Queue<IDeviceMemory> sizedPool))
                {
                    if (sizedPool.Count > 0)
                    {
                        IDeviceMemory result = sizedPool.Dequeue();

                        // HACK  bizarrely, Queue.Dequeue appears to sometimes return null, even when there are many elements in the queue,
                        // and when the queue is only ever accessed from one thread.
                        if (result != null)
                        {
                            return result;
                        }
                    }
                }
                else
                {
                    sizedPool = new Queue<IDeviceMemory>();
                    pools.Add(size, sizedPool);
                }

                CUdeviceptr buffer;
                try
                {
                    try
                    {
                        // If control flow gets to this point, sizedPool exists in the dictionary and is empty.
                        context.SetCurrent();
                        buffer = context.AllocateMemory(size);
                    }
                    catch (ManagedCuda.CudaException)
                    {
                        FreeMemory(false);
                        buffer = context.AllocateMemory(size);
                    }
                }
                catch (ManagedCuda.CudaException)
                {
                    FreeMemory(true);
                    buffer = context.AllocateMemory(size);
                }

                BasicDeviceMemory devMemory = null;
                devMemory = new BasicDeviceMemory(buffer, () =>
                {
                    lock (locker)
                    {
                        sizedPool.Enqueue(devMemory);
                    }
                });

                return devMemory;
            }
        }

        public void Dispose()
        {
            lock (locker)
            {
                foreach (KeyValuePair<long, Queue<IDeviceMemory>> kvp in pools)
                {
                    foreach (IDeviceMemory item in kvp.Value)
                    {
                        item.Free();
                    }
                }

                pools.Clear();
            }
        }

        private static long PadToAlignment(long size, long alignment)
        {
            return ((size + alignment - 1) / alignment) * alignment;
        }
    }
}
