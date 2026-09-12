// ActivityLog is process-wide (one app per process by design), so test classes that
// initialise it with their own temp root must not run at the same time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
