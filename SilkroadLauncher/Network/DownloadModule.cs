﻿using Pk2WriterAPI;
using SilkroadCommon.Download;
using SilkroadSecurityAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SilkroadLauncher.Network
{
    public static class DownloadModule
    {
        public static class Opcode
        {
            public const ushort
            CLIENT_FILE_REQUEST = 0x6004,

            SERVER_READY = 0x6005,
            SERVER_FILE_CHUNK = 0x1001,
            SERVER_FILE_COMPLETED = 0xA004;
        }
        /// <summary>
        /// The current file being downloaded
        /// </summary>
        private static FileStream m_FileStream;
        /// <summary>
        /// Get the files to be downloaded
        /// </summary>
        public static List<FileEntry> DownloadFiles { get; set; } = new List<FileEntry>();
        /// <summary>
        /// The version currently downloaded
        /// </summary>
        public static uint DownloadVersion { get; set; }

        #region Public Methods
        public static void Server_Ready(Packet p, Session s)
        {
            System.Diagnostics.Debug.WriteLine("Server_Ready");

            // Create a temporary directory to allocate the file
            Directory.CreateDirectory("Temp");

            // Create file download from server
            RequestFileDownload(s);
        }
        public static void Server_FileChunk(Packet p, Session s)
        {
            System.Diagnostics.Debug.WriteLine("Server_FileChunk");

            // Continue adding bytes to the file
            byte[] buffer = p.GetBytes();
            m_FileStream.WriteAsync(buffer, 0, buffer.Length);

            LauncherViewModel.Instance.UpdatingBytesDownloading += (uint)buffer.Length;
        }
        public static void Server_FileCompleted(Packet p, Session s)
        {
            byte result = p.ReadByte();

            // File downloaded
            var file = DownloadFiles[0];

            System.Diagnostics.Debug.WriteLine($"Server_FileCompleted: {file.ID} - Result:{result}");

            // Close the file
            m_FileStream.Close();
            m_FileStream.Dispose();

            // Process the file
            if (file.ImportToPk2)
            {
                // Get the Pk2 to open
                int pk2FileNameLength = file.Path.IndexOf("\\");
                string pk2FileName = file.Path.Remove(pk2FileNameLength);
                // Open the Pk2 and insert the file
                if (Pk2Writer.Open(pk2FileName, LauncherSettings.CLIENT_BLOWFISH_KEY))
                {
                    if (Pk2Writer.ImportFile(file.Path.Substring(pk2FileNameLength + 1) + "\\" + file.Name, "Temp\\" + file.ID))
                        System.Diagnostics.Debug.WriteLine($"File {file.Name} imported into the Pk2");
                    // Close the Pk2
                    Pk2Writer.Close();
                }
                // Delete the file
                File.Delete("Temp\\" + file.ID);
            }
            else
            {
                // Create an empty file to decompress (zlib)
                using (FileStream outFileStream = File.Create("Temp\\" + file.Name))
                {
                    using (FileStream inFileStream = new FileStream("Temp\\" + file.ID, FileMode.Open, FileAccess.Read))
                    {
                        // Read past unknown bytes (4) and zlib header bytes (2)
                        inFileStream.Seek(6, SeekOrigin.Begin);

                        using (DeflateStream zlib = new DeflateStream(inFileStream, CompressionMode.Decompress))
                        {
                            zlib.CopyTo(outFileStream);
                        }
                    }
                    // Delete decompressed file
                    File.Delete("Temp\\" + file.ID);
                }

                // Check file path
                if (file.Path != string.Empty) {
                    // Create directory if doesn't exists
                    if (!Directory.Exists(file.Path))
                        Directory.CreateDirectory(file.Path);
                    // Fix path for easy file moving
                    file.Path += "\\";
                }

                // Check if it's the Launcher to process it at the end
                var launcherFilename = Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                if (file.Path == string.Empty && file.Name.Equals(launcherFilename, StringComparison.InvariantCultureIgnoreCase))
                {
                    ForceMovingFile("Temp\\" + file.Name, "Temp\\_" + file.Name);
                }
                else
                {
                    // Move or replace from Temp to the folder required
                    ForceMovingFile("Temp\\" + file.Name, file.Path + file.Name);
                }
            }

            // Remove the file and delete it from Temp
            DownloadFiles.RemoveAt(0);
            
            // Continue protocol
            if (DownloadFiles.Count > 0)
            {
                RequestFileDownload(s);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Download finished!");

                // Update SV.T on media.pk2
                UpdateSilkroadVersion();

                // Update done!
                LauncherViewModel.Instance.IsUpdating = false;
                
                // Dispose pk2 writer
                Pk2Writer.Deinitialize();

                // Replace the Launcher if exists
                if (File.Exists("Temp\\_"+ Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)))
                {
                    // Replace launcher required
                    StartReplacer();
                }
                else
                {
                    // Update launcher version
                    LauncherViewModel.Instance.Version = DownloadVersion;
                    LauncherViewModel.Instance.CanStartGame = true;

                    // Stop connection
                    s.Stop();
                }
            }
        }
        #endregion

        #region Private Helpers
        /// <summary>
        /// Request a server file to be downloaded inmediatly
        /// </summary>
        private static void RequestFileDownload(Session s)
        {
            // Request the first file on list
            var file = DownloadFiles[0];

            // Init file holder
            m_FileStream = new FileStream("Temp\\" + file.ID, FileMode.Create, FileAccess.Write);

            // Request the first file
            Packet response = new Packet(Opcode.CLIENT_FILE_REQUEST, false);
            response.WriteUInt(file.ID);
            response.WriteUInt(0);
            s.Send(response);
        }
        /// <summary>
        /// Updates the SV.T file with the newer version
        /// </summary>
        private static void UpdateSilkroadVersion()
        {
            // Set the new version into pk2 automagically...
            if (Pk2Writer.Open(LauncherSettings.PATH_PK2_MEDIA, LauncherSettings.CLIENT_BLOWFISH_KEY))
            {
                var buffer = Encoding.ASCII.GetBytes(DownloadVersion.ToString());
                // Add blowfish minimum padding
                Array.Resize(ref buffer, 8);
                // Init blowfish for encoding
                Blowfish bf = new Blowfish();
                bf.Initialize(Encoding.ASCII.GetBytes("SILKROADVERSION"), 0, 8);
                // Create the SV.T file
                using (FileStream fs = File.Create("Temp\\SV.T"))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(buffer.Length);
                    // Encode it
                    buffer = bf.Encode(buffer, 0, buffer.Length);
                    // Add empty data
                    Array.Resize(ref buffer, 1024);
                    // Save it all
                    bw.Write(buffer);
                }
                // Update Pk2
                if (Pk2Writer.ImportFile("SV.T", "Temp\\SV.T"))
                    System.Diagnostics.Debug.WriteLine($"New version created and imported into the Pk2");
                // Close the Pk2
                Pk2Writer.Close();
                // Delete it
                File.Delete("Temp\\SV.T");
            }
        }
        /// <summary>
        /// Replaces the current executable with the new one downloaded
        /// </summary>
        private static void StartReplacer()
        {
            var launcherFilename = Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
            var launcherPath = Path.GetFullPath(launcherFilename);
            // Move my current executable to temp
            ForceMovingFile(launcherPath,"Temp\\" + launcherFilename + ".bkp");
            // Move the new launcher to the folder
            File.Move("Temp\\_" + launcherFilename, launcherPath);
            // Run the new launcher
            System.Diagnostics.Process.Start(launcherPath);
            // Close this one
            LauncherViewModel.Instance.Exit();
        }
        /// <summary>
        /// Move a file. The destination file will be replaced if exists.
        /// </summary>
        private static void ForceMovingFile(string sourceFileName, string destFileName)
        {
            if (File.Exists(destFileName))
                File.Delete(destFileName);
            File.Move(sourceFileName, destFileName);
        }
        #endregion
    }
}