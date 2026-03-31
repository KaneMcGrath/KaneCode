# Copilot Instructions

## Project Guidelines
- In this repository, prefer explicit type declarations instead of var.

## Testing Guidelines
- Use xUnit (`[Fact]`, `[Theory]`) for all tests. The test project is `KaneCode.Tests`.
- Mirror source structure: `Services/Foo.cs` → `KaneCode.Tests/Services/FooTests.cs`.
- Name tests by behavior: `WhenConditionThenExpectedOutcome`.
- Follow Arrange-Act-Assert. One behavior per test.
- Use xUnit `Assert` methods (no FluentAssertions).
- Extract logic out of ViewModels into testable services when the ViewModel method is hard to test (depends on WPF controls, complex DI, etc.). Pass simple abstractions or delegates instead of framework types.
- Prefer testing real implementations over mocks. Only mock external dependencies (file system, network, etc.) using delegates or simple interfaces.
- `[assembly: InternalsVisibleTo("KaneCode.Tests")]` may be used when needed to test internal members.