﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.PackageManagement;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.ProjectManagement;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using Wyam.Common.Tracing;

namespace Wyam.Configuration.NuGet
{
    internal class Package
    {
        private readonly NuGetFramework _currentFramework;
        private readonly string _packageId;
        private readonly IReadOnlyList<SourceRepository> _sourceRepositories;
        private readonly VersionRange _versionRange;  // null indicates any version
        private readonly bool _getLatest;
        private readonly bool _allowPrereleaseVersions;
        private readonly bool _allowUnlisted;
        private readonly bool _exclusive;  // Only the specified package sources should be used to find the package

        private NuGetVersion _versionMatch;

        public Package(
            NuGetFramework currentFramework,
            string packageId,
            IReadOnlyList<SourceRepository> sourceRepositories,
            string versionRange,
            bool getLatest,
            bool allowPrereleaseVersions,
            bool allowUnlisted,
            bool exclusive)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }
            if (string.IsNullOrWhiteSpace(packageId))
            {
                throw new ArgumentException(nameof(packageId));
            }
            if (getLatest && !string.IsNullOrEmpty(versionRange))
            {
                throw new ArgumentException("Can not specify both a version and the latest package");
            }

            _currentFramework = currentFramework;
            _packageId = packageId;
            _sourceRepositories = sourceRepositories;
            if (!string.IsNullOrEmpty(versionRange) && !VersionRange.TryParse(versionRange, out _versionRange))
            {
                throw new ArgumentException(nameof(versionRange));
            }
            _getLatest = getLatest;
            _allowPrereleaseVersions = allowPrereleaseVersions;
            _allowUnlisted = allowUnlisted;
            _exclusive = exclusive;
        }

        public async Task ResolveVersion(
            SourceRepository localRepository,
            IReadOnlyList<SourceRepository> remoteRepositories,
            bool updatePackages,
            ILogger logger)
        {
            string versionRangeString = _versionRange == null ? string.Empty : " " + _versionRange;
            Trace.Verbose($"Resolving package version for {_packageId}{versionRangeString} (with dependencies)");
            IReadOnlyList<SourceRepository> sourceRepositories = GetSourceRepositories(remoteRepositories);
            NuGetVersion versionMatch = null;
            if (!_getLatest && !updatePackages)
            {
                // Get the latest matching version in the local repository
                versionMatch = await GetLatestMatchingVersion(localRepository, logger);
            }
            if (versionMatch != null)
            {
                Trace.Verbose($"Package {_packageId}{versionRangeString} is satisfied by version {versionMatch.ToNormalizedString()}");
            }
            else
            {
                // The package either wasn't installed locally, the local version didn't match, or we requested a package update
                // Get the latest remote version, but only if we actually have remote repositories
                if (sourceRepositories?.Count > 0)
                {
                    versionMatch = await GetLatestMatchingVersion(sourceRepositories, logger);
                }
                if (versionMatch == null)
                {
                    Trace.Verbose($"Package {_packageId}{versionRangeString} was not found on any remote source repositories");
                }
            }
            _versionMatch = versionMatch;
        }

        public async Task Install(
            IReadOnlyList<SourceRepository> remoteRepositories,
            InstalledPackagesCache installedPackages,
            NuGetPackageManager packageManager,
            ISettings settings)
        {
            if (_versionMatch == null)
            {
                return;
            }
            IReadOnlyList<SourceRepository> sourceRepositories = GetSourceRepositories(remoteRepositories);
            using (Trace.WithIndent().Verbose($"Installing package {_packageId} {_versionMatch.ToNormalizedString()}"))
            {
                // Check if this package was already installed in a previous run
                PackageIdentity packageIdentity = new PackageIdentity(_packageId, _versionMatch);
                if (installedPackages.VerifyPackage(packageIdentity, packageManager))
                {
                    Trace.Verbose($"Package {_packageId} {_versionMatch.ToNormalizedString()} was already installed");
                    return;
                }

                // If not, go ahead and install it
                using (installedPackages.AddPackage(packageIdentity, _currentFramework))
                {
                    ResolutionContext resolutionContext = new ResolutionContext(
                        DependencyBehavior.Lowest, _allowPrereleaseVersions, _allowUnlisted, VersionConstraints.None);
                    ILogger logger = new NuGetLogger();
                    ClientPolicyContext clientPolicyContext = ClientPolicyContext.GetClientPolicy(settings, logger);
                    INuGetProjectContext projectContext = new NuGetProjectContext
                    {
                        PackageExtractionContext = new global::NuGet.Packaging.PackageExtractionContext(
                            global::NuGet.Packaging.PackageSaveMode.Defaultv3,
                            global::NuGet.Packaging.XmlDocFileSaveMode.None,
                            clientPolicyContext,
                            logger)
                    };
                    await packageManager.InstallPackageAsync(
                        packageManager.PackagesFolderNuGetProject,
                        packageIdentity,
                        resolutionContext,
                        projectContext,
                        sourceRepositories,
                        Array.Empty<SourceRepository>(),
                        CancellationToken.None);
                    Trace.Verbose($"Installed package {_packageId} {_versionMatch.ToNormalizedString()}");
                }
            }
        }

        private IReadOnlyList<SourceRepository> GetSourceRepositories(IReadOnlyList<SourceRepository> remoteRepositories)
        {
            return _exclusive
                ? _sourceRepositories
                : _sourceRepositories?.Concat(remoteRepositories).Distinct().ToList() ?? remoteRepositories;
        }

        private async Task<NuGetVersion> GetLatestMatchingVersion(IEnumerable<SourceRepository> sourceRepositories, ILogger logger)
        {
            NuGetVersion[] versionMatches = await Task.WhenAll(sourceRepositories.Select(x => GetLatestMatchingVersion(x, logger)));
            return versionMatches
                .Where(x => x != null)
                .DefaultIfEmpty()
                .Max();
        }

        private async Task<NuGetVersion> GetLatestMatchingVersion(SourceRepository sourceRepository, ILogger logger)
        {
            try
            {
                DependencyInfoResource dependencyInfoResource = await sourceRepository.GetResourceAsync<DependencyInfoResource>();
                using (SourceCacheContext sourceCacheContext = new SourceCacheContext())
                {
                    IEnumerable<SourcePackageDependencyInfo> dependencyInfo = await dependencyInfoResource.ResolvePackages(
                        _packageId, _currentFramework, sourceCacheContext, logger, CancellationToken.None);
                    return dependencyInfo
                        .Select(x => x.Version)
                        .Where(x => x != null && (_versionRange?.Satisfies(x) != false))
                        .DefaultIfEmpty()
                        .Max();
                }
            }
            catch (Exception ex)
            {
                Trace.Warning($"Could not get latest version for package {_packageId} from source {sourceRepository}: {ex.Message}");
                return null;
            }
        }
    }
}
