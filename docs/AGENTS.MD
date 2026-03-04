# Agents

KaneCode is a .NET 8 WPF desktop application for managing developer tools and lightweight AI-assisted workflows. The repository contains UI controls, theming, and AI chat/agent services used to prototype interactive agents and integrations.

## Code style

- Use explicit types

  Prefer explicit type declarations instead of `var` so code is clear about the intended type. For example, use:

  ```csharp
  int count = 5;
  MyService service = new MyService();
  ```

  instead of:

  ```csharp
  var count = 5;
  var service = new MyService();
  ```
