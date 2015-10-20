﻿using System.Collections.Generic;

namespace Voron.Impl.FreeSpace
{
    public interface IFreeSpaceHandling
    {
        long? TryAllocateFromFreeSpace(LowLevelTransaction tx, int num);
        List<long> AllPages(LowLevelTransaction tx);
        void FreePage(LowLevelTransaction tx, long pageNumber);
    }
}