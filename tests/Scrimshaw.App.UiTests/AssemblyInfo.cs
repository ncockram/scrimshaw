// UI tests share one mouse, keyboard and screen: one collection for the whole assembly, so they never
// run in parallel (Phase 4: two app windows overlapped and screenshots caught the wrong one).
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
