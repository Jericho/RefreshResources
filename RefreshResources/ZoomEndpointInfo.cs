using Microsoft.OpenApi.Models;

namespace RefreshResources
{
	internal class ZoomEndpointInfo
	{
		public string Title { get; set; }

		public string Group { get; set; }

		public string Name { get; set; }

		public string Sample { get; set; }

		public string Path { get; set; }

		public OperationType Operation { get; set; }

		public string Summary { get; set; }

		public string Tag { get; set; }

		public string ResponseJson { get; set; }

		public string GeneratedCSharpCode { get; set; }

		public string MethodName { get; set; }

		public bool IsHandled { get; set; }
	}
}
