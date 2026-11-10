# CSharp.Refactor

Functional refactoring hints with one-click quick fixes for C#. The
extension ships the CSharp.Refactor Roslyn analyzers as a Visual Studio
analyzer asset, so every C# project opened in the IDE gets the suggestions
and light bulbs with no project change and no effect on builds.

The rules care about correctness (races, swallowed exceptions, leaked
disposables, unobserved tasks), measured performance (allocations that need
not happen, repeated work) and clear functional idiom — and deliberately not
about naming, layout or conventions. A suggestion is Info severity: it marks
an opportunity, and never gates a build. The priority rules, which point at
likely defects, show as warnings.

Requires Visual Studio 2022 17.14 or newer: the analyzers are built against
its Roslyn.

The same rules run in `dotnet build` through the `CSharp.Refactor.Analyzers`
NuGet package, and the `csharp-refactor` dotnet tool applies them in bulk,
build-verified. Rules and reasoning: https://github.com/Thorium/csharp-refactor/blob/main/Rules.md
