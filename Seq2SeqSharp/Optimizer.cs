﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks; 
namespace Seq2SeqSharp
{

    public class Optimizer
    {
        public static float decay_rate = 0.999f;
        public static float smooth_eps = 1e-8f;

        public Vector<float> vecDecayRate = new Vector<float>(decay_rate);
        public Vector<float> vecSmoothEPS = new Vector<float>(smooth_eps);

        List<WeightMatrix> step_cache = new List<WeightMatrix>();


        public void setp(List<WeightMatrix> model, float step_size, float regc, float clipval)
        {
            var vecMaxClipval = new Vector<float>(clipval);
            var vecMinClipval = new Vector<float>(-clipval);

            Parallel.ForEach(model, k =>
            {
                if (k != null)
                {
                    var m = k; // mat ref 
                    var s = k.Cash;
                    var n = m.Weight.Length;
                    var i = 0;
                    var moreItems = (n % Vector<float>.Count);

                    while (i < n - moreItems)
                    {
                        var vecMDWI = new Vector<float>(m.Gradient, i);

                        vecMDWI = Vector.Min(vecMDWI, vecMaxClipval);
                        vecMDWI = Vector.Max(vecMDWI, vecMinClipval);

                        var vecS = new Vector<float>(s, i);
                        vecS = vecS * vecDecayRate + (Vector<float>.One - vecDecayRate) * vecMDWI * vecMDWI;
                        vecS.CopyTo(s, i);

                        var vecMW = new Vector<float>(m.Weight, i);
                        var vecDelta = -step_size * vecMDWI / Vector.SquareRoot(vecS + vecSmoothEPS) - regc * vecMW;

                        vecMW += vecDelta;
                        vecMW.CopyTo(m.Weight, i);

                        Vector<float>.Zero.CopyTo(m.Gradient, i);


                        i += Vector<float>.Count;
                    }

                    while (i < n)
                    {
                        // rmsprop adaptive learning rate
                        var mdwi = m.Gradient[i];

                        // gradient clip
                        if (mdwi > clipval)
                        {
                            mdwi = clipval;
                        }
                        if (mdwi < -clipval)
                        {
                            mdwi = -clipval;
                        }

                        s[i] = (float)(s[i] * decay_rate + (1.0 - decay_rate) * mdwi * mdwi);
                        var delta = (float)(-step_size * mdwi / Math.Sqrt(s[i] + smooth_eps) - regc * m.Weight[i]);

                        // update (and regularize)
                        m.Weight[i] += delta;

                        m.Gradient[i] = 0; // reset gradients for next iteration
                        i++;
                    }
                }
            });
        }      
    }
}
