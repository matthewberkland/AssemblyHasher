﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AssemblyHasher
{
    public static class FileHasher
    {
        public static string Hash(params string[] filenames)
        {
            return Hash(filenames);
        }

        public static string Hash(bool ignoreVersions, out Manifest manifest, List<string> filenames, bool keepTempFiles)
        {
            //see if any of the "filenames" are actualy directories, if so expand them and add to the filenames list
            var otherfiles = new List<string>();
            var foundDirs = new List<string>();
            var files = filenames.ToList();
            foreach (var file in files)
            {
                Trace.WriteLine(string.Format("Compiling file list for {0}", file));
                if (File.GetAttributes(file).HasFlag(FileAttributes.Directory))
                {
                    //expand out the files in it
                    var temp = Directory.GetFiles(file, "*.*", SearchOption.AllDirectories);
                    //add to list
                    otherfiles.AddRange(temp);
                    foundDirs.Add(file);
                }
            }

            //combine things...
            if (otherfiles.Any())
            {
                //remove found directories 
                foundDirs.ForEach(f => files.Remove(f));

                //add it
                files.AddRange(otherfiles);

                //sort it
                files.Sort();

                //set it
                filenames = files.ToList();

            }

            manifest = new Manifest();

            Trace.WriteLine("Starting to hash files");
            //begin the hashing...
            using (var hashService = Murmur.MurmurHash.Create128())
            {
                foreach (var filename in filenames)
                {
                    var extension = Path.GetExtension(filename).ToLowerInvariant();
                    if (extension == ".dll" || extension == ".exe")
                    {
                        Trace.WriteLine(string.Format("Disassembling file: {0}", Path.GetFileNameWithoutExtension(filename)));
                        var disassembled = Disassembler.Disassemble(filename);

                        if (disassembled.Successful)
                        {
                            Trace.WriteLine("File was dissasembled to " + disassembled.ILFilename);
                            AddFileToHash(disassembled.ILFilename, hashService, AssemblySourceCleanup.GetFilter(AssemblySourceCleanup.FileTypes.IL, ignoreVersions));
                            HashIndividualFile(manifest, disassembled.ILFilename, AssemblySourceCleanup.GetFilter(AssemblySourceCleanup.FileTypes.IL, ignoreVersions), nameHint: Path.GetFileName(filename));

                            Trace.WriteLine("Extracting resources from " + filename);
                            foreach (var resource in disassembled.Resources)
                            {
                                AddFileToHash(resource, hashService, AssemblySourceCleanup.GetFilter(resource, ignoreVersions));
                                HashIndividualFile(manifest, resource, AssemblySourceCleanup.GetFilter(resource, ignoreVersions), isCompiled: true);
                            }
                        }
                        else
                        {
                            //the filename sent, wasn't .NET compatible, can't be disassembled. Hash the bytes of it instead
                            AddFileToHash(disassembled.ILFilename, hashService, AssemblySourceCleanup.GetFilter(disassembled.ILFilename, ignoreVersions));
                            HashIndividualFile(manifest, disassembled.ILFilename, AssemblySourceCleanup.GetFilter(disassembled.ILFilename, ignoreVersions));
                        }

                        if (!keepTempFiles)
                        {
                            Trace.WriteLine("Removing temporary files");
                            disassembled.Delete();
                        }
                    }
                    else
                    {
                        AddFileToHash(filename, hashService, AssemblySourceCleanup.GetFilter(filename, ignoreVersions));
                        HashIndividualFile(manifest, filename, AssemblySourceCleanup.GetFilter(filename, ignoreVersions));
                    }
                }

                Trace.WriteLine("Computing master hash for entire assembly set");
                hashService.TransformFinalBlock(new byte[0], 0, 0);
                var finalHash = Convert.ToBase64String(hashService.Hash);
                manifest.MasterHash = finalHash;
                return manifest.MasterHash;
            }

        }


        private static void HashIndividualFile(Manifest manfiest, string path, StreamFilter filter = null, Encoding encoding = null, bool isCompiled = false, string nameHint = null)
        {
            using (var singleHash = Murmur.MurmurHash.Create128())
            {
                Trace.WriteLine("Computing hash for " + path);
                AddFileToHash(path, singleHash, filter);
                var hash = FindHash(singleHash);
                Trace.WriteLine("Completed hash, result was " + hash);
                manfiest.Components.Add(new ChildItem { Path = isCompiled ? Path.GetFileName(path) : nameHint ?? path, Hash = hash });
            }
        }

        private static string FindHash(HashAlgorithm hashService)
        {
            hashService.TransformFinalBlock(new byte[0], 0, 0);
            return Convert.ToBase64String(hashService.Hash);
            hashService.Clear();
        }

        private static void AddFileToHash(string filename, HashAlgorithm hashService, StreamFilter filter = null, Encoding encoding = null)
        {
            if (filter == null || filter == StreamFilter.None)
            {
                using (var stream = File.OpenRead(filename))
                {
                    var buffer = new byte[1200000];
                    var bytesRead = stream.Read(buffer, 0, buffer.Length);
                    while (bytesRead > 1)
                    {
                        hashService.TransformBlock(buffer, 0, bytesRead, buffer, 0);
                        bytesRead = stream.Read(buffer, 0, buffer.Length);
                    }
                }
            }
            else
            {
                if (encoding == null)
                {
                    if (Path.GetExtension(filename).Equals(".res", StringComparison.InvariantCultureIgnoreCase))
                    {
                        encoding = Encoding.Unicode;
                    }
                    else
                    {
                        encoding = Encoding.Default;
                    }
                }
                using (var stream = File.OpenRead(filename))
                {
                    using (var reader = new StreamReader(stream, encoding))
                    {
                        foreach (var line in filter.ReadAllLines(reader))
                        {
                            var lineBuffer = encoding.GetBytes(line);
                            hashService.TransformBlock(lineBuffer, 0, lineBuffer.Length, lineBuffer, 0);
                        }
                    }
                }
            }
        }
    }
}
