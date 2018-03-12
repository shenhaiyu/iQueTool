﻿using System;
using System.Collections.Generic;
using System.Text;
using iQueTool.Structs;

namespace iQueTool.Files
{
    public class iQueNand
    {
        public const int BLOCK_SZ = 0x4000;
        public const int NUM_FAT_BLOCKS = 0x10;
        public const int NUM_SYS_AREA_BLOCKS = 0x40;
        public const int NUM_BLOCKS_IN_FAT = 0x1000;
        public const int NUM_FS_ENTRIES = 0x199;
        public const int FS_HEADER_ADDR = 0x3FF4;

        public const int MAGIC_BBFS = 0x42424653;
        public const int MAGIC_BBFL = 0x4242464C;

        private IO io;
        public string FilePath;
        public int InodesOffset = 0; // only public ique dump is sorta mangled, inodes start 0x10 bytes away from where they should for some reason
        public bool SkipVerifyFsChecksums = false;

        public List<int> FsBlocks;
        public List<int> FsBadBlocks;

        // todo: put next 3 fields in an iQueNandFs class?
        public List<iQueFsHeader> FsHeaders;
        // read table as signed so we can treat end-of-chain and sys-allocated blocks as negative numbers
        // if there were more than 0x8000 blocks in a nand we really shouldn't do this, but luckily there aren't
        public List<List<short>> FsAllocationTables;
        public List<List<iQueFsInode>> FsInodes;

        public iQueFsHeader MainFs;
        public int MainFsBlock;
        public int MainFsIndex = -1;

        public List<iQueFsInode> MainFsInodes
        {
            get
            {
                if (MainFsIndex < 0)
                    return null;

                return FsInodes[MainFsIndex];
            }
        }

        public List<short> MainFsAllocTable
        {
            get
            {
                if (MainFsIndex < 0)
                    return null;

                return FsAllocationTables[MainFsIndex];
            }
        }

        // different sections/files of the NAND
        public iQueKernel SKSA;

        public bool HasPrivateData = false;
        public iQuePrivateData PrivateData; // depot.sys
        
        public iQueArrayFile<iQueTitleData> Tickets; // ticket.sys
        public iQueArrayFile<iQueCertificateRevocation> CRL; // crl.sys
        public iQueCertCollection Certs; // cert.sys

        public iQueNand(string filePath)
        {
            FilePath = filePath;
            io = new IO(filePath);
        }

        public void SeekToBlock(int blockIdx)
        {
            io.Stream.Position = blockIdx * BLOCK_SZ;
        }

        public int GetInodeIdx(string fileName)
        {
            if (MainFsIndex < 0)
                return -1;

            for(int i = 0; i < MainFsInodes.Count; i++)
                if(MainFsInodes[i].NameString.ToLower() == fileName.ToLower())
                    return i;

            return -1;
        }

        public short[] GetBlockChain(short blockIdx, int maxBlocks = int.MaxValue)
        {
            var chain = new List<short>();
            var curBlock = blockIdx;

            do
            {
                chain.Add(curBlock);
                curBlock = MainFsAllocTable[curBlock];
            }
            while (curBlock >= 0 && (maxBlocks == int.MaxValue || chain.Count < maxBlocks)); // if curBlock is negative (eg 0xFFFD or 0xFFFF) stop following the chain

            return chain.ToArray();
        }

        public byte[] GetSKSAData()
        {
            io.Stream.Position = 0x0;
            return io.Reader.ReadBytes(NUM_SYS_AREA_BLOCKS * BLOCK_SZ);
        }

        public byte[] GetInodeData(iQueFsInode inode)
        {
            var data = new byte[inode.Size];
            var chain = GetBlockChain(inode.BlockIdx);

            for(int i = 0; i < chain.Length; i++)
            {
                io.Stream.Position = chain[i] * BLOCK_SZ;

                int numRead = BLOCK_SZ;
                if (i + 1 == chain.Length)
                    numRead = (int)(inode.Size % BLOCK_SZ);
                if (numRead == 0)
                    numRead = BLOCK_SZ;

                var blockData = io.Reader.ReadBytes(numRead);
                Array.Copy(blockData, 0, data, i * BLOCK_SZ, numRead);
            }

            return data;
        }

        public bool Read()
        {
            var numBlocks = (int)(io.Stream.Length / BLOCK_SZ);

            if(numBlocks != NUM_BLOCKS_IN_FAT)
                return false; // invalid image, maybe the NAND you downloaded needs to be trimmed from 0x3FFC0000 to 0x7FFC0000, for a perfect 0x40000000 byte image?

            io.Stream.Position = 0;
            SKSA = new iQueKernel(io);
            SKSA.Read();

            var ret = ReadFilesystem();
            if (!ret)
                return false; // failed to find valid FAT

            int idx = GetInodeIdx("cert.sys");
            if (idx >= 0)
            {
                var data = GetInodeData(MainFsInodes[idx]);
                Certs = new iQueCertCollection(data);
                if (iQueCertCollection.MainCollection == null)
                    iQueCertCollection.MainCollection = new iQueCertCollection(data); // MainCollection wasn't loaded already, so lets try loading it from this nand (yolo)
            }

            idx = GetInodeIdx("depot.sys");
            HasPrivateData = idx >= 0;
            if(HasPrivateData)
            {
                var data = GetInodeData(MainFsInodes[idx]);
                PrivateData = Shared.BytesToStruct<iQuePrivateData>(data);
                //PrivateData.EndianSwap();
            }

            idx = GetInodeIdx("ticket.sys");
            if(idx >= 0)
            {
                var data = GetInodeData(MainFsInodes[idx]);
                Tickets = new iQueArrayFile<iQueTitleData>(data);
                for (int i = 0; i < Tickets.Count; i++)
                    Tickets[i] = Tickets[i].EndianSwap();
            }

            idx = GetInodeIdx("crl.sys");
            if (idx >= 0)
            {
                var data = GetInodeData(MainFsInodes[idx]);
                CRL = new iQueArrayFile<iQueCertificateRevocation>(data);
                foreach (var crl in CRL)
                    crl.EndianSwap();
            }

            // todo:
            // recrypt.sys
            // timer.sys
            // sig.db

            return true;
        }

        private bool ReadFilesystem()
        {
            FsHeaders = new List<iQueFsHeader>();
            FsBlocks = new List<int>();
            FsBadBlocks = new List<int>();
            FsInodes = new List<List<iQueFsInode>>();
            FsAllocationTables = new List<List<short>>();

            int latestSeqNo = -1;
            for (int i = 0; i < NUM_FAT_BLOCKS; i++)
            {
                var blockNum = (NUM_BLOCKS_IN_FAT - 1) - i; // read FAT area from end to beginning
                SeekToBlock(blockNum);

                io.Stream.Position += FS_HEADER_ADDR;
                var header = io.Reader.ReadStruct<iQueFsHeader>();
                header.EndianSwap();
                if (header.Magic != MAGIC_BBFS) // todo: && header.Magic != MAGIC_BBFL, once we know what BBFL/"linked fats" actually are
                    continue;

                if(!SkipVerifyFsChecksums && InodesOffset == 0) // only care about fs checksum if this is a proper dump and we aren't using hacky InodesOffset hack
                    if (!VerifyFsChecksum(header, blockNum))
                    {
                        FsBadBlocks.Add(blockNum);
                        continue; // bad FS checksum :(
                    }

                FsHeaders.Add(header);
                FsBlocks.Add(blockNum);

                if (header.SeqNo > latestSeqNo)
                {
                    MainFs = header;
                    MainFsBlock = blockNum;
                    MainFsIndex = FsHeaders.Count - 1;
                    latestSeqNo = header.SeqNo;
                }

                io.Stream.Position = blockNum * BLOCK_SZ;
                var allocTable = new List<short>();
                for (int y = 0; y < NUM_BLOCKS_IN_FAT; y++)
                    allocTable.Add((short)(io.Reader.ReadUInt16().EndianSwap()));

                int numEntries = NUM_FS_ENTRIES;
                if (InodesOffset > 0)
                {
                    io.Stream.Position += InodesOffset; // skip weird truncated inode if needed
                    numEntries--; // and now we'll have to read one less entry
                }

                // now begin reading inodes
                var inodes = new List<iQueFsInode>();
                for (int y = 0; y < numEntries; y++)
                {
                    var inode = io.Reader.ReadStruct<iQueFsInode>();
                    inode.EndianSwap();
                    inodes.Add(inode);
                }

                FsAllocationTables.Add(allocTable);
                FsInodes.Add(inodes);
            }

            return latestSeqNo >= 0;
        }

        public bool VerifyFsChecksum(iQueFsHeader fatHeader, int fatBlockIdx)
        {
            io.Stream.Position = (fatBlockIdx * BLOCK_SZ);

            ushort sum = 0;
            for (int i = 0; i < 0x1FFF; i++)
                sum += io.Reader.ReadUInt16().EndianSwap();

            sum += fatHeader.CheckSum; // should be EndianSwap'd already by ReadFilesystem
            return sum == 0xCAD7;
        }

        public bool VerifyFsChecksum(int fsIndex)
        {
            if (fsIndex < 0 || fsIndex >= FsHeaders.Count || fsIndex >= FsBlocks.Count)
                return false;

            var fatHeader = FsHeaders[fsIndex];
            var fatBlockIdx = FsBlocks[fsIndex];

            return VerifyFsChecksum(fatHeader, fatBlockIdx);
        }

        public override string ToString()
        {
            return ToString(false);
        }

        public string ToString(bool formatted, bool fsInfoOnly = true, bool allFsInfo = false)
        {
            var b = new StringBuilder();
            b.AppendLine("iQueNand:");

            b.AppendLine($"MainFs (@ 0x{(MainFsBlock * BLOCK_SZ):X}):");
            b.AppendLine($"  SeqNo: {MainFs.SeqNo}");
            b.AppendLine($"  CheckSum: {MainFs.CheckSum}");
            b.AppendLine($"  NumFiles: {MainFsInodes.FindAll(s => s.Valid == 1).Count}");
            b.AppendLine();
            for(int i = 0; i < MainFsInodes.Count; i++)
            {
                if (MainFsInodes[i].Valid != 1)
                    continue;
                b.AppendLine("  " + MainFsInodes[i].ToString(i));
            }

            if(allFsInfo)
            {
                for(int i = 0; i < FsHeaders.Count; i++)
                {
                    if (FsBlocks[i] == MainFsBlock)
                        continue;

                    b.AppendLine();

                    b.AppendLine($"Fs-{i} (@ 0x{(FsBlocks[i] * BLOCK_SZ):X})");
                    b.AppendLine($"  SeqNo: {FsHeaders[i].SeqNo}");
                    b.AppendLine($"  CheckSum: {FsHeaders[i].CheckSum}");
                    b.AppendLine($"  NumFiles: {FsInodes[i].FindAll(s => s.Valid == 1).Count}");
                    b.AppendLine();
                    for (int y = 0; y < FsInodes[i].Count; y++)
                    {
                        if (FsInodes[i][y].Valid != 1)
                            continue;
                        b.AppendLine("  " + FsInodes[i][y].ToString(y));
                    }
                }
            }

            if (fsInfoOnly)
                return b.ToString();

            b.AppendLine();

            if (HasPrivateData)
                b.AppendLine(PrivateData.ToString(formatted, "iQuePrivateData (depot.sys)"));
            else
                b.AppendLine("Failed to read iQuePrivateData struct from depot.sys :(");

            if (SKSA != null)
            {
                b.AppendLine(SKSA.ToString(formatted));

                b.AppendLine();
            }

            if (Tickets != null)
            {
                b.AppendLine($"Num tickets: {Tickets.Count}");
                for (int i = 0; i < Tickets.Count; i++)
                    b.AppendLine(Tickets[i].ToString(formatted, $"iQueTitleData-{i}"));
            }
            else
                b.AppendLine("Failed to read iQueTitleData array from ticket.sys :(");

            if (CRL != null)
                b.AppendLine(CRL.ToString(formatted));
            else
                b.AppendLine("Failed to read iQueCertificateRevocation array from crl.sys :(");

            if (Certs != null)
                b.AppendLine(Certs.ToString(formatted));
            else
                b.AppendLine("Failed to read iQueCertificate array from cert.sys :(");

            return b.ToString();
        }
    }
}
