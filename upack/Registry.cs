﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Inedo.ProGet.UPack
{
    public sealed class Registry
    {
        public static Registry Machine => new Registry(Environment.OSVersion.Platform == PlatformID.Unix ? "/var/lib/upack" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "upack"));
        public static Registry User => new Registry(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".upack"));
        public static Registry Unregistered => new Registry(null);

        private readonly string path;

        public Registry(string path)
        {
            this.path = path;
        }

        private async Task<T> RetryAsync<T>(Func<Task<T>> f)
        {
            RegistryLockedException lastEx = null;

            for (int tries = 0; tries < 1000; tries++)
            {
                try
                {
                    return await f();
                }
                catch (RegistryLockedException ex)
                {
                    lastEx = ex;

                    await Console.Error.WriteAsync(ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    await Console.Error.WriteAsync('.');
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    await Console.Error.WriteAsync('.');
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    await Console.Error.WriteLineAsync('.');
                }
            }

            throw lastEx;
        }

        private async Task<T> WithLockAsync<T>(Func<Task<T>> f, string description)
        {
            if (description != null && description.Contains("\n"))
            {
                throw new ArgumentException("Description must not contain line breaks.");
            }

            var lockPath = Path.Combine(this.path, ".lock");
            if (!Directory.Exists(this.path))
            {
                Directory.CreateDirectory(path);
            }

            Stream stream;
            try
            {
                stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException ex)
            {
                if (!File.Exists(lockPath))
                {
                    throw;
                }

                var lastWrite = File.GetLastWriteTime(lockPath);
                if (lastWrite + TimeSpan.FromSeconds(10) < DateTime.Now)
                {
                    stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    if (File.GetLastWriteTime(lockPath) != lastWrite && stream.Length != 0)
                    {
                        string lockDescription = null;

                        try
                        {
                            using (var reader = new StreamReader(stream))
                            {
                                lockDescription = await reader.ReadLineAsync().ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                        }

                        throw new RegistryLockedException("Registry is locked: " + (lockDescription ?? "No description provided."), ex);
                    }
                    stream.SetLength(0);
                }
                else
                {
                    var lockLines = File.ReadAllLines(lockPath);
                    throw new RegistryLockedException("Registry is locked: " + (lockLines.FirstOrDefault() ?? "No description provided."), ex);
                }
            }

            var guid = Guid.NewGuid();

            using (var writer = new StreamWriter(stream))
            using (var currentProcess = Process.GetCurrentProcess())
            {
                await writer.WriteLineAsync($"[{currentProcess.Id}] {description ?? currentProcess.ProcessName}").ConfigureAwait(false);
                await writer.WriteLineAsync(guid.ToString()).ConfigureAwait(false);
            }

            T t = await f();

            try
            {
                var lockLines = File.ReadAllLines(lockPath);
                if (lockLines.Length != 2 || lockLines[1] != guid.ToString())
                {
                    throw new RegistryLockedException("Registry lock token did not match.");
                }
                File.Delete(lockPath);
            }
            catch (FileNotFoundException)
            {
                throw new RegistryLockedException("Registry lock file was deleted by another process.");
            }

            return t;
        }

        public async Task<IList<InstalledPackage>> ListInstalledPackagesAsync()
        {
            if (this.path == null)
            {
                return new InstalledPackage[0];
            }

            return await this.RetryAsync(() => this.WithLockAsync(() =>
            {
                var serializer = new DataContractJsonSerializer(typeof(List<InstalledPackage>));
                try
                {
                    using (var stream = new FileStream(Path.Combine(path, "installedPackages.json"), FileMode.Open, FileAccess.Read))
                    {
                        return Task.FromResult((List<InstalledPackage>)serializer.ReadObject(stream));
                    }
                }
                catch (FileNotFoundException)
                {
                    return Task.FromResult(new List<InstalledPackage>());
                }
            }, "listing installed packages"));
        }

        private string GetCachedPackagePath(string group, string name, UniversalPackageVersion version)
        {
            return Path.Combine(path, "packageCache", (group ?? string.Empty).Replace('/', '$') + "$" + name, name + "." + version.ToString() + ".upack");
        }

        public async Task RegisterPackageAsync(string group, string name, UniversalPackageVersion version,
            string intendedPath, string feedUrl, NetworkCredential feedAuthentication = null,
            string installationReason = null, string installedUsing = null, string installedBy = null)
        {
            if (this.path == null)
            {
                return;
            }

            await this.RetryAsync(() => this.WithLockAsync(() =>
            {
                List<InstalledPackage> packages;
                var serializer = new DataContractJsonSerializer(typeof(List<InstalledPackage>));
                try
                {
                    using (var stream = new FileStream(Path.Combine(path, "installedPackages.json"), FileMode.Open, FileAccess.Read))
                    {
                        packages = (List<InstalledPackage>)serializer.ReadObject(stream);
                    }
                }
                catch (FileNotFoundException)
                {
                    packages = new List<InstalledPackage>();
                }

                var package = packages.Find(pkg =>
                {
                    return string.Equals(pkg.Group, group, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pkg.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        pkg.Version == version;
                });

                if (package == null)
                {
                    package = new InstalledPackage
                    {
                        Group = group,
                        Name = name,
                        Version = version,
                        Path = intendedPath,
                        FeedUrl = feedUrl,
                        InstallationDate = DateTime.UtcNow,
                        InstallationReason = installationReason,
                        InstalledUsing = installedUsing ?? (Assembly.GetEntryAssembly().GetName().Name + "/" + Assembly.GetEntryAssembly().GetName().Version.ToString()),
                        InstalledBy = installedBy
                    };

                    packages.Add(package);

                    using (var stream = new FileStream(Path.Combine(path, "installedPackages.json"), FileMode.Create, FileAccess.Write))
                    {
                        serializer.WriteObject(stream, packages);
                    }
                }

                return Task.FromResult((object)null);
            }, $"checking installation status of {group}/{name} {version}"));
        }

        private async Task CachePackageToDisk(Stream stream, string group, string name, UniversalPackageVersion version, string feedUrl, NetworkCredential feedAuthentication = null)
        {
            using (var client = new HttpClient(new HttpClientHandler
            {
                UseDefaultCredentials = feedAuthentication == null,
                Credentials = feedAuthentication,
                PreAuthenticate = true
            })
            {
                Timeout = Timeout.InfiniteTimeSpan
            })
            {
                string encodedName = Uri.EscapeUriString(name);
                if (!string.IsNullOrEmpty(group))
                {
                    encodedName = Uri.EscapeUriString(group) + '/' + encodedName;
                }

                var url = $"{feedUrl.TrimEnd('/')}/download/{encodedName}/{Uri.EscapeDataString(version.ToString())}";

                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    await response.Content.CopyToAsync(stream);
                }
            }
        }

        public async Task<Stream> GetOrDownloadAsync(string group, string name, UniversalPackageVersion version, string feedUrl, NetworkCredential feedAuthentication = null, bool cache = true)
        {
            if (this.path == null || !cache)
            {
                var tempFile = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 0, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                try
                {
                    await this.CachePackageToDisk(tempFile, group, name, version, feedUrl, feedAuthentication);
                    tempFile.Position = 0;
                }
                catch
                {
                    try
                    {
                        tempFile.Dispose();
                    }
                    catch
                    {
                    }
                    throw;
                }
                return tempFile;
            }

            var cachePath = this.GetCachedPackagePath(group, name, version);

            if (!File.Exists(cachePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));

                var stream = new FileStream(cachePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                try
                {
                    using (stream)
                    {
                        await this.CachePackageToDisk(stream, group, name, version, feedUrl, feedAuthentication);
                    }
                }
                catch
                {
                    try
                    {
                        File.Delete(cachePath);
                    }
                    catch
                    {
                    }
                    throw;
                }
            }

            return new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        }
    }

    [DataContract]
    public sealed class InstalledPackage
    {
        [DataMember(IsRequired = false, Name = "group")]
        public string Group { get; set; }

        [DataMember(IsRequired = true, Name = "name")]
        public string Name { get; set; }

        internal string GroupAndName => string.IsNullOrEmpty(this.Group) ? this.Name : this.Group + "/" + this.Name;

        public UniversalPackageVersion Version { get; set; }

        [DataMember(IsRequired = true, Name = "version")]
        public string VersionString
        {
            get => this.Version.ToString();
            set => this.Version = UniversalPackageVersion.Parse(value);
        }

        // The absolute path on disk where the package was installed to.
        [DataMember(IsRequired = true, Name = "path")]
        public string Path { get; set; }

        // An absolute URL of the universal feed where the package was installed from.
        [DataMember(IsRequired = false, Name = "feed")]
        public string FeedUrl { get; set; }

        private DateTime? installationDate;
        private string installationDateString;

        // The UTC date when the package was installed.
        public DateTime? InstallationDate
        {
            get => this.installationDate;
            set
            {
                if (value.HasValue)
                {
                    this.installationDate = value;
                    this.installationDateString = value?.ToString("o");
                }
                else
                {
                    this.installationDate = null;
                    this.installationDateString = null;
                }
            }
        }

        [DataMember(IsRequired = false, Name = "installationDate")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public string InstallationDateString
        {
            get => this.installationDateString;
            set
            {
                this.installationDate = string.IsNullOrEmpty(value) ? null : (DateTime?)DateTime.Parse(value);
                this.installationDateString = value;
            }
        }

        // The reason or purpose of the installation.
        [DataMember(IsRequired = false, Name = "installationReason")]
        public string InstallationReason { get; set; }

        // The mechanism used to install the package. There are no format restrictions, but we recommend treating it like a User Agent string and including the tool name and version.
        [DataMember(IsRequired = false, Name = "installedUsing")]
        public string InstalledUsing { get; set; }

        // The person or service that performed the installation.
        [DataMember(IsRequired = false, Name = "installedBy")]
        public string InstalledBy { get; set; }
    }

    [Serializable]
    internal class RegistryLockedException : Exception
    {
        public RegistryLockedException()
        {
        }

        public RegistryLockedException(string message) : base(message)
        {
        }

        public RegistryLockedException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected RegistryLockedException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
