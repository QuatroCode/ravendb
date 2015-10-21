﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voron.Impl.FreeSpace;
using Xunit;

namespace Voron.Tests.Trees
{
    public class FreeSpaceTest : StorageTest
    {
        [Fact]
        public void WillBeReused()
        {
            var random = new Random();
            var buffer = new byte[512];
            random.NextBytes(buffer);

            using (var tx = Env.WriteTransaction())
            {
                var tree = tx.CreateTree("foo");
                for (int i = 0; i < 25; i++)
                {
                    tree.Add(i.ToString("0000"), new MemoryStream(buffer));
                }

                tx.Commit();
            }
            var before = Env.Stats();

            using (var tx = Env.WriteTransaction())
            {
                var tree = tx.ReadTree("foo");
                for (int i = 0; i < 25; i++)
                {
                    tree.Delete(i.ToString("0000"));
                }

                tx.Commit();
            }

            var old = Env.NextPageNumber;
            using (var tx = Env.WriteTransaction())
            {
                var tree = tx.ReadTree("foo");
                for (int i = 0; i < 25; i++)
                {
                    tree.Add(i.ToString("0000"), new MemoryStream(buffer));
                }

                tx.Commit();
            }

            var after = Env.Stats();

            Assert.Equal(after.RootPages, before.RootPages);

            Assert.True(Env.NextPageNumber - old < 2, "This test will not pass until we finish merging the free space branch");
        }

        [Fact]
        public void ShouldReturnProperPageFromSecondSection()
        {
            using (var tx = Env.WriteTransaction())
            {
                Env.FreeSpaceHandling.FreePage(tx.LowLevelTransaction, FreeSpaceHandling.NumberOfPagesInSection + 1);

                tx.Commit();
            }

            using (var tx = Env.WriteTransaction())
            {
                Assert.Equal(FreeSpaceHandling.NumberOfPagesInSection + 1, Env.FreeSpaceHandling.TryAllocateFromFreeSpace(tx.LowLevelTransaction, 1));
            }
        }

        [Fact]
        public void CanReuseMostOfFreePages_RemainingOnesCanBeTakenToHandleFreeSpace()
        {
            const int maxPageNumber = 4000000;
            const int numberOfFreedPages = 100;
            var random = new Random(3);
            var freedPages = new HashSet<long>();

            using (var tx = Env.WriteTransaction())
            {
                tx.LowLevelTransaction.State.NextPageNumber = maxPageNumber + 1;

                tx.Commit();
            }

            for (int i = 0; i < numberOfFreedPages; i++)
            {
                long pageToFree;
                do
                {
                    pageToFree = random.Next(0, maxPageNumber);
                } while (freedPages.Add(pageToFree) == false);

                using (var tx = Env.WriteTransaction())
                {
                    Env.FreeSpaceHandling.FreePage(tx.LowLevelTransaction, pageToFree);

                    tx.Commit();
                }
            }

            // we cannot expect that all freed pages will be available for a reuse
            // some freed pages can be used internally by free space handling
            // 80% should be definitely a safe value

            var minNumberOfFreePages = numberOfFreedPages * 0.8;

            for (int i = 0; i < minNumberOfFreePages; i++)
            {
                using (var tx = Env.WriteTransaction())
                {
                    var page = Env.FreeSpaceHandling.TryAllocateFromFreeSpace(tx.LowLevelTransaction, 1);

                    Assert.NotNull(page);
                    Assert.True(freedPages.Remove(page.Value));

                    tx.Commit();
                }
            }
        }

        [Fact]
        public void FreeSpaceHandlingShouldNotReturnPagesThatAreAlreadyAllocated()
        {
            const int maxPageNumber = 400000;
            const int numberOfFreedPages = 60;
            var random = new Random(2);
            var freedPages = new HashSet<long>();

            using (var tx = Env.WriteTransaction())
            {
                tx.LowLevelTransaction.State.NextPageNumber = maxPageNumber + 1;

                tx.Commit();
            }

            for (int i = 0; i < numberOfFreedPages; i++)
            {
                long pageToFree;
                do
                {
                    pageToFree = random.Next(0, maxPageNumber);
                } while (freedPages.Add(pageToFree) == false);

                using (var tx = Env.WriteTransaction())
                {
                    Env.FreeSpaceHandling.FreePage(tx.LowLevelTransaction, pageToFree);

                    tx.Commit();
                }
            }

            var alreadyReused = new List<long>();

            do
            {
                using (var tx = Env.WriteTransaction())
                {
                    var page = Env.FreeSpaceHandling.TryAllocateFromFreeSpace(tx.LowLevelTransaction, 1);

                    if (page == null)
                    {
                        break;
                    }

                    Assert.False(alreadyReused.Contains(page.Value), "Free space handling returned a page number that has been already allocated. Page number: " + page);
                    Assert.True(freedPages.Remove(page.Value));

                    alreadyReused.Add(page.Value);

                    tx.Commit();
                }
            } while (true);
        }

        [Fact]
        public void CanGetListOfAllFreedPages()
        {
            const int maxPageNumber = 10000;
            const int numberOfFreedPages = 5000;
            var random = new Random();
            var freedPages = new HashSet<long>();
            var allocatedPages = new List<long>(maxPageNumber);

            using (var tx = Env.WriteTransaction())
            {
                for (int i = 0; i < maxPageNumber; i++)
                {
                     allocatedPages.Add(tx.LowLevelTransaction.AllocatePage(1).PageNumber);
                }

                tx.Commit();
            }

            for (int i = 0; i < numberOfFreedPages; i++)
            {
                using (var tx = Env.WriteTransaction())
                {

                    do
                    {
                        var idx = random.Next(0, allocatedPages.Count);
                        if (allocatedPages[idx] == -1)
                            break;
                        freedPages.Add(allocatedPages[idx]);
                        Env.FreeSpaceHandling.FreePage(tx.LowLevelTransaction, allocatedPages[idx]);
                        allocatedPages[idx] = -1;


                    } while (true);


                    tx.Commit();
                }
            }

            using (var tx = Env.WriteTransaction())
            {
                var retrievedFreePages = Env.FreeSpaceHandling.AllPages(tx.LowLevelTransaction);

                //freedPages.ExceptWith(Env.FreeSpaceHandling.GetFreePagesOverheadPages(tx.LowLevelTransaction)); // need to take into account that some of free pages might be used for free space handling
                var sorted = freedPages.OrderBy(x => x).ToList();

                Assert.Equal(sorted, retrievedFreePages);
            }
        }
    }
}