namespace RefreshResources
{
	internal record ZoomWebhookInfo
	{
		public bool IsDeprecated { get; init; }

		public string Description { get; init; }

		public string OperationId { get; init; }

		public string Summary { get; init; }

		public string Title { get; init; }

		public string Group { get; init; }

		public string Name { get; init; }

		public string Sample { get; init; }

		public bool IsHandled { get; init; }
	}
}
