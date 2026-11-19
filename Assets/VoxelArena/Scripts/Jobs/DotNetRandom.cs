using Unity.Collections;

/// <summary>
/// Тот же генератор, что System.Random с сидом (вычитающий генератор Кнута), но без управляемой памяти:
/// работает в Burst. Последовательность чисел совпадает с System.Random при том же сиде.
/// </summary>
public struct DotNetRandom
{
    const int MBig = int.MaxValue;
    const int MSeed = 161803398;

    FixedList512Bytes<int> seedArray;
    int inext;
    int inextp;

    public DotNetRandom(int seed)
    {
        seedArray = default;
        for (int i = 0; i < 56; i++)
            seedArray.Add(0);

        int subtraction = seed == int.MinValue ? int.MaxValue : (seed < 0 ? -seed : seed);
        int mj = MSeed - subtraction;
        seedArray[55] = mj;
        int mk = 1;
        for (int i = 1; i < 55; i++)
        {
            int ii = 21 * i % 55;
            seedArray[ii] = mk;
            mk = mj - mk;
            if (mk < 0)
                mk += MBig;
            mj = seedArray[ii];
        }
        for (int k = 1; k < 5; k++)
        {
            for (int i = 1; i < 56; i++)
            {
                int value = seedArray[i] - seedArray[1 + (i + 30) % 55];
                if (value < 0)
                    value += MBig;
                seedArray[i] = value;
            }
        }
        inext = 0;
        inextp = 21;
    }

    int InternalSample()
    {
        int locINext = inext + 1;
        if (locINext >= 56)
            locINext = 1;
        int locINextp = inextp + 1;
        if (locINextp >= 56)
            locINextp = 1;

        int result = seedArray[locINext] - seedArray[locINextp];
        if (result == MBig)
            result--;
        if (result < 0)
            result += MBig;

        seedArray[locINext] = result;
        inext = locINext;
        inextp = locINextp;
        return result;
    }

    public double NextDouble() => InternalSample() * (1.0 / MBig);

    /// <summary>Как System.Random.Next(maxValue): от 0 до maxValue, не включая</summary>
    public int Next(int maxValue) => (int)(NextDouble() * maxValue);
}
