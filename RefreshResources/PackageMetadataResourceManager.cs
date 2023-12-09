using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using System.Collections.Concurrent;

namespace RefreshResources
{
	internal sealed class PackageMetadataResourceManager
	{
		private const string NUGET_URL = "https://api.nuget.org/v3/index.json";

		private static readonly ConcurrentDictionary<string, PackageMetadataResource> _clients = new();

		static PackageMetadataResourceManager()
		{
			_clients.TryAdd(NUGET_URL, GetNewClient(NUGET_URL));
		}

		public static PackageMetadataResource GetClient(string repositoryUrl = null)
		{
			var repoUrl = string.IsNullOrEmpty(repositoryUrl) ? NUGET_URL : repositoryUrl.TrimEnd('/').EnsureEndsWith("/index.json");
			return _clients.GetOrAdd(repoUrl, url => GetNewClient(url));
		}

		private static PackageMetadataResource GetNewClient(string repositoryUrl)
		{
			return Repository.Factory.GetCoreV3(repositoryUrl).GetResource<PackageMetadataResource>();
		}
	}
}
