using Xunit;

// SavegameSchemaRegistry is static and shared across all tests.
// Parallel execution causes tests to stomp on each other's registrations.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
