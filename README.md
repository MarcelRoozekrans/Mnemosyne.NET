# Mnemosyne.NET

[![NuGet](https://img.shields.io/nuget/v/Mnemosyne.NET.svg?logo=nuget&label=Mnemosyne.NET)](https://www.nuget.org/packages/Mnemosyne.NET)
[![CI](https://img.shields.io/github/actions/workflow/status/MarcelRoozekrans/Mnemosyne.NET/ci.yml?branch=main&logo=github&label=build)](https://github.com/MarcelRoozekrans/Mnemosyne.NET/actions/workflows/ci.yml)
[![Docs](https://img.shields.io/github/actions/workflow/status/MarcelRoozekrans/Mnemosyne.NET/docs.yml?branch=main&logo=readthedocs&label=docs)](https://marcelroozekrans.github.io/Mnemosyne.NET/)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/Mnemosyne.NET)](LICENSE)

Procedural memory for .NET agents: store, find and reuse the problem-solving
procedures that worked, as **Reasonlets**.

Documentation: <https://marcelroozekrans.github.io/Mnemosyne.NET/>

- [Getting started](https://marcelroozekrans.github.io/Mnemosyne.NET/getting-started) and the [store-and-recall sample](https://marcelroozekrans.github.io/Mnemosyne.NET/samples)
- [Concepts](https://marcelroozekrans.github.io/Mnemosyne.NET/category/concepts): Reasonlets, versions, scopes and subjects, ranking
- [Guides](https://marcelroozekrans.github.io/Mnemosyne.NET/category/guides): the PostgreSQL store, data lifecycle, telemetry

| Package | What it holds |
|---|---|
| [`Mnemosyne.NET.Abstractions`](src/Mnemosyne.NET.Abstractions/README.md) | The records and the interfaces |
| [`Mnemosyne.NET`](src/Mnemosyne.NET/README.md) | The default facade and ranking |
| [`Mnemosyne.NET.PostgreSql`](src/Mnemosyne.NET.PostgreSql/README.md) | The PostgreSQL and pgvector store |

Part of the same family as [Minos.NET](https://github.com/MarcelRoozekrans/Minos.NET)
(decisions), [Rag.NET](https://github.com/MarcelRoozekrans/Rag.NET) (knowledge),
[Thalos.NET](https://github.com/MarcelRoozekrans/Thalos.NET) (agent framework) and
[Daedalus.NET](https://github.com/MarcelRoozekrans/Daedalus.NET) (execution runtime).

| Library | Memory kind | Question it answers |
|---|---|---|
| Rag.NET | Knowledge | What do we know? |
| Minos.NET | Decisions | What decision should be made? |
| **Mnemosyne.NET** | **Procedures** | **How did we solve this before, and did it work?** |

Status: pre-release.

## License

MIT
