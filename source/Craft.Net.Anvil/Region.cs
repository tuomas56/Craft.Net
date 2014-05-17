using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using fNbt;
using Ionic.Zlib;
using Craft.Net.Common;

namespace Craft.Net.Anvil
{
    /// <summary>
    /// Represents a 32x32 area of <see cref="Chunk"/> objects.
    /// Not all of these chunks are represented at any given time, and
    /// will be loaded from disk or generated when the need arises.
    /// </summary>
    public class Region : IDisposable
    {
        // In chunks
        public const int Width = 32, Depth = 32;

        /// <summary>
        /// The currently loaded chunk list.
        /// </summary>
        public Dictionary<Coordinates2D, Chunk> Chunks { get; set; }
        /// <summary>
        /// The location of this region in the overworld.
        /// </summary>
        public Coordinates2D Position { get; set; }

        public World World { get; set; }

        private Stream regionFile { get; set; }

        /// <summary>
        /// Creates a new Region for server-side use at the given position using
        /// the provided terrain generator.
        /// </summary>
        public Region(Coordinates2D position, World world)
        {
            Chunks = new Dictionary<Coordinates2D, Chunk>();
            Position = position;
            World = world;
        }

        /// <summary>
        /// Creates a region from the given region file.
        /// </summary>
        public Region(Coordinates2D position, World world, string file) : this(position, world)
        {
            if (File.Exists(file))
                regionFile = File.Open(file, FileMode.OpenOrCreate);
            else
            {
                regionFile = File.Open(file, FileMode.OpenOrCreate);
                CreateRegionHeader();
            }
        }

        /// <summary>
        /// Retrieves the requested chunk from the region, or
        /// generates it if a world generator is provided.
        /// </summary>
        /// <param name="position">The position of the requested local chunk coordinates.</param>
        public Chunk GetChunk(Coordinates2D position)
        {
            // TODO: This could use some refactoring
            lock (Chunks)
            {
                if (!Chunks.ContainsKey(position))
                {
                    if (regionFile != null)
                    {
                        // Search the stream for that region
                        lock (regionFile)
                        {
                            var chunkData = GetChunkFromTable(position);
                            if (chunkData == null)
                            {
                                if (World.WorldGenerator == null)
                                    throw new ArgumentException("The requested chunk is not loaded.", "position");
                                GenerateChunk(position);
                                return Chunks[position];
                            }
                            regionFile.Seek(chunkData.Item1, SeekOrigin.Begin);
                            /*int length = */new MinecraftStream(regionFile).ReadInt32(); // TODO: Avoid making new objects here, and in the WriteInt32
                            int compressionMode = regionFile.ReadByte();
                            switch (compressionMode)
                            {
                                case 1: // gzip
                                    break;
                                case 2: // zlib
                                    var nbt = new NbtFile();
                                    nbt.LoadFromStream(regionFile, NbtCompression.ZLib, null);
                                    var chunk = Chunk.FromNbt(nbt);
                                    Chunks.Add(position, chunk);
                                    break;
                                default:
                                    throw new InvalidDataException("Invalid compression scheme provided by region file.");
                            }
                        }
                    }
                    else if (World.WorldGenerator == null)
                        throw new ArgumentException("The requested chunk is not loaded.", "position");
                    else
                        GenerateChunk(position);
                }
                return Chunks[position];
            }
        }

        /// <summary>
        /// Retrieves the requested chunk from the region, without using the
        /// world generator if it does not exist.
        /// </summary>
        /// <param name="position">The position of the requested local chunk coordinates.</param>
        public Chunk GetChunkWithoutGeneration(Coordinates2D position)
        {
            // TODO: This could use some refactoring
            lock (Chunks)
            {
                if (!Chunks.ContainsKey(position))
                {
                    if (regionFile != null)
                    {
                        // Search the stream for that region
                        lock (regionFile)
                        {
                            var chunkData = GetChunkFromTable(position);
                            if (chunkData == null)
                                return null;
                            regionFile.Seek(chunkData.Item1, SeekOrigin.Begin);
                            /*int length = */new MinecraftStream(regionFile).ReadInt32(); // TODO: Avoid making new objects here, and in the WriteInt32
                            int compressionMode = regionFile.ReadByte();
                            switch (compressionMode)
                            {
                                case 1: // gzip
                                    break;
                                case 2: // zlib
                                    var nbt = new NbtFile();
                                    nbt.LoadFromStream(regionFile, NbtCompression.ZLib, null);
                                    var chunk = Chunk.FromNbt(nbt);
                                    Chunks.Add(position, chunk);
                                    break;
                                default:
                                    throw new InvalidDataException("Invalid compression scheme provided by region file.");
                            }
                        }
                    }
                    else if (World.WorldGenerator == null)
                        throw new ArgumentException("The requested chunk is not loaded.", "position");
                    else
                        GenerateChunk(position);
                }
                return Chunks[position];
            }
        }

        public void GenerateChunk(Coordinates2D position)
        {
            var globalPosition = (Position * new Coordinates2D(Width, Depth)) + position;
            var chunk = World.WorldGenerator.GenerateChunk(globalPosition);
            chunk.IsModified = true;
            chunk.X = globalPosition.X;
            chunk.Z = globalPosition.Z;
            Chunks.Add(position, chunk);
        }

        /// <summary>
        /// Sets the chunk at the specified local position to the given value.
        /// </summary>
        public void SetChunk(Coordinates2D position, Chunk chunk)
        {
            if (!Chunks.ContainsKey(position))
                Chunks.Add(position, chunk);
            chunk.IsModified = true;
            chunk.X = position.X;
            chunk.Z = position.Z;
            chunk.LastAccessed = DateTime.Now;
            Chunks[position] = chunk;
        }

        /// <summary>
        /// Saves this region to the specified file.
        /// </summary>
        public void Save(string file)
        {
            if(File.Exists(file))
                regionFile = regionFile ?? File.Open(file, FileMode.OpenOrCreate);
            else
            {
                regionFile = regionFile ?? File.Open(file, FileMode.OpenOrCreate);
                CreateRegionHeader();
            }
            Save();
        }

        /// <summary>
        /// Saves this region to the open region file.
        /// </summary>
        public void Save()
        {
            lock (Chunks)
            {
                lock (regionFile)
                {
                    var toRemove = new List<Coordinates2D>();
                    foreach (var kvp in Chunks)
                    {
                        var chunk = kvp.Value;
                        if (chunk.IsModified)
                        {
                            var data = chunk.ToNbt();
                            byte[] raw = data.SaveToBuffer(NbtCompression.ZLib);

                            var header = GetChunkFromTable(kvp.Key);
                            if (header == null || header.Item2 > raw.Length)
                                header = AllocateNewChunks(kvp.Key, raw.Length);

                            regionFile.Seek(header.Item1, SeekOrigin.Begin);
                            new MinecraftStream(regionFile).WriteInt32(raw.Length);
                            regionFile.WriteByte(2); // Compressed with zlib
                            regionFile.Write(raw, 0, raw.Length);

                            chunk.IsModified = false;
                        }
                        if ((DateTime.Now - chunk.LastAccessed).TotalMinutes > 5)
                            toRemove.Add(kvp.Key);
                    }
                    regionFile.Flush();
                    // Unload idle chunks
                    foreach (var chunk in toRemove)
                        Chunks.Remove(chunk);
                }
            }
        }

        #region Stream Helpers

        private const int ChunkSizeMultiplier = 4096;
        private Tuple<int, int> GetChunkFromTable(Coordinates2D position) // <offset, length>
        {
            int tableOffset = ((position.X % Width) + (position.Z % Depth) * Width) * 4;
            regionFile.Seek(tableOffset, SeekOrigin.Begin);
            byte[] offsetBuffer = new byte[4];
            regionFile.Read(offsetBuffer, 0, 3);
            Array.Reverse(offsetBuffer);
            int length = regionFile.ReadByte();
            int offset = BitConverter.ToInt32(offsetBuffer, 0) << 4;
            if (offset == 0 || length == 0)
                return null;
            return new Tuple<int, int>(offset,
                length * ChunkSizeMultiplier);
        }

        private void CreateRegionHeader()
        {
            regionFile.Write(new byte[8192], 0, 8192);
            regionFile.Flush();
        }

        private Tuple<int, int> AllocateNewChunks(Coordinates2D position, int length)
        {
            // Expand region file
            regionFile.Seek(0, SeekOrigin.End);
            int dataOffset = (int)regionFile.Position;

            length /= ChunkSizeMultiplier;
            length++;
            regionFile.Write(new byte[length * ChunkSizeMultiplier], 0, length * ChunkSizeMultiplier);

            // Write table entry
            int tableOffset = ((position.X % Width) + (position.Z % Depth) * Width) * 4;
            regionFile.Seek(tableOffset, SeekOrigin.Begin);

            byte[] entry = BitConverter.GetBytes(dataOffset >> 4);
            entry[0] = (byte)length;
            Array.Reverse(entry);
            regionFile.Write(entry, 0, entry.Length);

            return new Tuple<int, int>(dataOffset, length * ChunkSizeMultiplier);
        }

        #endregion

        public static string GetRegionFileName(Coordinates2D position)
        {
            return string.Format("r.{0}.{1}.mca", position.X, position.Z);
        }

        public void UnloadChunk(Coordinates2D position)
        {
            Chunks.Remove(position);
        }

        public void Dispose()
        {
            if (regionFile == null)
                return;
            lock (regionFile)
            {
                regionFile.Flush();
                regionFile.Close();
            }
        }
    }
}
