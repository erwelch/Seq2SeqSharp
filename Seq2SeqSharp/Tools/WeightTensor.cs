﻿using AdvUtils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TensorSharp;

namespace Seq2SeqSharp.Tools
{
    public enum NormType
    {
        None,
        Uniform,
        Normal
    }

    [Serializable]
    public class WeightTensor : IWeightTensor, IDisposable
    {
        public long[] Sizes { get; set; }

        public int Rows
        {
            get => (int)Sizes[0];
            set => Sizes[0] = value;
        }
        public int Columns
        {
            get => (int)Sizes[1];
            set => Sizes[1] = value;
        }

        public string Name { get; set; }
        public bool IsTrainable { get; set; }

        public int DeviceId { get; set; }

        private IAllocator m_allocator;

        private Tensor m_TWeight = null;
        private Tensor m_TGradient = null;
        private static readonly object locker = new object();

        private bool releasedWeight = false;
        private bool releasedGradient = false;
        private IComputeGraph m_computeGraphToBind;

        private string m_GradientSetName = "None";

        public Tensor TWeight
        {
            get
            {
                if (releasedWeight)
                {
                    throw new Exception($"The weight '{Name}' has been released, you cannot access it.");
                }

                if (m_TWeight == null)
                {
                    m_TWeight = new Tensor(m_allocator, DType.Float32, Sizes);
                }

                return m_TWeight;
            }
            set
            {
                if (m_TWeight != null)
                {
                    throw new Exception($"Please call ReleaseWeight function before assign a new value to weight '{Name}'.");
                }

                m_TWeight = value;
                releasedWeight = false;
            }
        }

        public Tensor TGradient
        {
            get
            {
                if (releasedGradient)
                {
                    throw new Exception($"The gradient '{Name}' has been released, you cannot access it.");
                }

                if (m_TGradient == null)
                {
                    m_TGradient = new Tensor(m_allocator, DType.Float32, Sizes);
                    Ops.Fill(m_TGradient, 0.0f);

                    m_GradientSetName = "Get";
                }

                return m_TGradient;
            }

            set
            {
                if (m_TGradient != null)
                {                   
                    throw new Exception($"Please call ReleaseGradient function before assign a new value to gradient '{Name}'. This gradient was set by '{m_GradientSetName}'");
                }

                m_TGradient = value;
                releasedGradient = false;
            }
        }

        public WeightTensor(long[] sizes, int deviceId, string name = "", bool isTrainable = false, NormType normal = NormType.None, IComputeGraph graphToBind = null)
        {
            Name = name;
            DeviceId = deviceId;
            IsTrainable = isTrainable;
            m_allocator = TensorAllocator.Allocator(DeviceId);
            Sizes = sizes;

            if (graphToBind != null)
            {
                m_computeGraphToBind = graphToBind;
                m_computeGraphToBind.Bind(this);
            }

            if (normal == NormType.Uniform)
            {
                SeedSource seedSource = new SeedSource(DateTime.Now.Millisecond);

                var std = (float)Math.Sqrt(6.0 / (double)(Rows + Columns));
                Ops.RandomUniform(TWeight, seedSource, -std, std);

            }
            else if (normal == NormType.Normal)
            {
                SeedSource seedSource = new SeedSource(DateTime.Now.Millisecond);
                var std = 1.0 / Math.Sqrt((double)Columns);
                Ops.RandomNormal(TWeight, seedSource, 0.0f, (float)std);
            }
        }

        public WeightTensor(long[] sizes, float c, int deviceId, string name = "", bool isTrainable = false)
        {
            Name = name;
            DeviceId = deviceId;
            IsTrainable = isTrainable;
            Sizes = sizes;
            m_allocator = TensorAllocator.Allocator(DeviceId);

            TWeight = new Tensor(m_allocator, DType.Float32, Sizes);
            Ops.Fill(TWeight, c);
        }


        public void UnbindFromComputeGraph()
        {
            if (m_computeGraphToBind != null)
            {
                m_computeGraphToBind.Unbind(this);
            }
        }

        public int GetDeviceId()
        {
            return DeviceId;
        }

        public INeuralUnit CloneToDeviceAt(int deviceId)
        {
            return new WeightTensor(Sizes, deviceId, Name, IsTrainable, graphToBind: m_computeGraphToBind);
        }

        public void ZeroGradient()
        {
            Ops.Fill(TGradient, 0.0f);
        }

        public void CleanWeight()
        {
            Ops.Fill(TWeight, 0.0f);
        }

        public float GetWeightAt(int offset)
        {
            return TWeight.GetElementAsFloat(0, offset);
        }

        public void SetWeightAt(float val, int offset)
        {
            TWeight.SetElementAsFloat(val, 0, offset);
        }


        public void SetGradientAt(float val, int offset)
        {
            TGradient.SetElementAsFloat(val, 0, offset);
        }

        public void SetWeightAtRow(int row, float[] val)
        {
            TWeight.SetElementsAsFloat(val, row, 0);
        }

        public void CopyWeightsToGradients(IWeightTensor src)
        {
            WeightTensor m = src as WeightTensor;

            if (m_TGradient != null)
            {
                m_TGradient.Dispose();
            }

            m_TGradient = m.TWeight.CopyRef();

            m_GradientSetName = "CopyWeightsToGradients";
        }

        public void CopyWeightsFrom(IWeightTensor src)
        {
            WeightTensor m = src as WeightTensor;

            Ops.Copy(TWeight, m.TWeight);
        }

        public void AddGradientFrom(IWeightTensor src)
        {
            WeightTensor m = src as WeightTensor;

            lock (locker)
            {
                Tensor t = new Tensor(TGradient.Allocator, DType.Float32, Sizes);
                Ops.Copy(t, m.TGradient);
                Ops.Add(TGradient, TGradient, t);

                t.Dispose();
            }
        }

        public float[] ToWeightArray()
        {
            return TWeight.GetElementsAsFloat(Rows * Columns);
        }

        public float[] ToGradientArray()
        {
            return TGradient.GetElementsAsFloat(Rows * Columns);
        }


        public void AddSoftmaxGradient(WeightTensor src, bool inPlace = false)
        {
            if (m_TGradient == null)
            {
                m_allocator = TensorAllocator.Allocator(DeviceId);
                m_TGradient = new Tensor(m_allocator, DType.Float32, Sizes);
                Ops.SoftmaxGrad(m_TGradient, src.TGradient, src.TWeight, false);

                m_GradientSetName = "AddSoftmaxGradient";
            }
            else
            {
                Ops.SoftmaxGrad(m_TGradient, src.TGradient, src.TWeight, !inPlace);
            }
        }

        public void CopyOrAddGradient(WeightTensor src)
        {
            if (m_TGradient == null)
            {
                m_allocator = TensorAllocator.Allocator(DeviceId);
                m_TGradient = new Tensor(m_allocator, DType.Float32, Sizes);
                Ops.Copy(m_TGradient, src.TGradient);

                m_GradientSetName = "CopyOrAddGradient_WeightTensor";
            }
            else
            {
                Ops.Add(m_TGradient, m_TGradient, src.TGradient);
            }
        }

        public void CopyOrAddGradient(Tensor src, string callerName = "")
        {
            if (m_TGradient == null)
            {
                m_allocator = TensorAllocator.Allocator(DeviceId);
                m_TGradient = new Tensor(m_allocator, DType.Float32, Sizes);
                Ops.Copy(m_TGradient, src);

                m_GradientSetName = $"CopyOrAddGradient_Tensor_CalledBy_{callerName}";
            }
            else
            {
                Ops.Add(m_TGradient, m_TGradient, src);
            }
        }

        public void AddMulGradient(Tensor w, Tensor g, bool inPlace = false)
        {
            if (m_TGradient == null)
            {
                m_allocator = TensorAllocator.Allocator(DeviceId);
                m_TGradient = new Tensor(m_allocator, DType.Float32, Sizes);
                Ops.Mul(m_TGradient, w, g);

                m_GradientSetName = "AddMulGrdient";
            }
            else
            {
                if (inPlace)
                {
                    Ops.Mul(m_TGradient, w, g);
                }
                else
                {
                    Ops.AddMul(m_TGradient, m_TGradient, w, g);
                }
            }
        }

        public void AddSigmoidGradient(WeightTensor src)
        {
            if (m_TGradient == null)
            {
                m_allocator = TensorAllocator.Allocator(DeviceId);
                m_TGradient = new Tensor(m_allocator, DType.Float32, Sizes);
                Ops.SigmoidD(m_TGradient, src.TWeight, src.TGradient);

                m_GradientSetName = "AddSigmoidGradient";
            }
            else
            {
                Ops.AddSigmoidD(m_TGradient, m_TGradient, src.TWeight, src.TGradient);
            }
        }


        public void AddTanhGradient(WeightTensor src)
        {
            if (m_TGradient == null)
            {
                m_allocator = TensorAllocator.Allocator(DeviceId);
                m_TGradient = new Tensor(m_allocator, DType.Float32, Sizes);

                Ops.TanhD(m_TGradient, src.TWeight, src.TGradient);

                m_GradientSetName = "AddTanhGradient";
            }
            else
            {
                Ops.AddTanhD(m_TGradient, m_TGradient, src.TWeight, src.TGradient);
            }
        }

        public List<int> GetTopNMaxWeightIdx(int topN)
        {
            float[] weights = ToWeightArray();
            FixedSizePriorityQueue<ComparableItem<int>> q = new FixedSizePriorityQueue<ComparableItem<int>>(topN, new ComparableItemComparer<int>(true));

            for (int i = 0; i < weights.Length; i++)
            {
                q.Enqueue(new ComparableItem<int>(weights[i], i));
            }

            return q.Select(x => x.Value).ToList();
        }

        public void SetWeightArray(float[] v)
        {
            TWeight.SetElementsAsFloat(v);
        }

        public void SetGradientArray(float[] v)
        {
            TGradient.SetElementsAsFloat(v);
        }

        public WeightTensor CopyWeightsRef(string name)
        {
            WeightTensor result = new WeightTensor(Sizes, DeviceId, name)
            {
                m_TWeight = m_TWeight.CopyRef()
            };

            return result;
        }

        public void Dispose()
        {
            ReleaseWeight();
            ReleaseGradient();
        }

        public void ReleaseWeight()
        {
            if (m_TWeight != null)
            {
                m_TWeight.Dispose();
                m_TWeight = null;
                releasedWeight = true;
            }
        }

        public void ReleaseGradient()
        {
            if (m_TGradient != null)
            {
                m_TGradient.Dispose();
                m_TGradient = null;
                releasedGradient = true;
            }
        }

        public void Save(Stream stream)
        {
            float[] floatArray1 = ToWeightArray();

            // create a byte array and copy the floats into it...
            byte[] byteArray = new byte[floatArray1.Length * 4];
            Buffer.BlockCopy(floatArray1, 0, byteArray, 0, byteArray.Length);

            stream.Write(byteArray, 0, byteArray.Length);
        }

        public void Load(Stream stream)
        {
            int size = Rows * Columns;
            byte[] byteArray = new byte[size * 4];
            stream.Read(byteArray, 0, byteArray.Length);

            float[] floatArray2 = new float[byteArray.Length / 4];
            Buffer.BlockCopy(byteArray, 0, floatArray2, 0, byteArray.Length);

            SetWeightArray(floatArray2);
        }

        public List<IWeightTensor> GetParams()
        {
            if (IsTrainable)
            {
                return new List<IWeightTensor>() { this };
            }
            else
            {
                return new List<IWeightTensor>();
            }
        }
    }
}
