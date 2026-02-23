# DotNetDumpExtension

A custom [dotnet-dump](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-dump) extension that adds new diagnostic commands to the `dotnet-dump analyze` REPL.

## Requirements

[dotnet-dump](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-dump) must be installed before building:

```
dotnet tool install -g dotnet-dump
```

## Build

**Windows**
```cmd
build\build.bat
```

**Linux / macOS**
```bash
bash build/build.sh
```

The output DLL is written to `src/DotNetDumpExtension/bin/Release/netstandard2.0/DotNetDumpExtension.dll`.

## Usage

### 1. Load the extension via environment variable

```powershell
# Windows PowerShell
$env:DOTNET_DIAGNOSTIC_EXTENSIONS = "<path-to-project>\bin\Release\netstandard2.0\DotNetDumpExtension.dll"
dotnet-dump analyze <path-to-dump>
```

```cmd
:: Windows Command Prompt
set DOTNET_DIAGNOSTIC_EXTENSIONS=<path-to-project>\bin\Release\netstandard2.0\DotNetDumpExtension.dll
dotnet-dump analyze <path-to-dump>
```

```bash
# Linux / macOS
export DOTNET_DIAGNOSTIC_EXTENSIONS=<path-to-project>/bin/Release/netstandard2.0/DotNetDumpExtension.dll
dotnet-dump analyze <path-to-dump>
```

### 2. Run the commands inside the REPL

#### `heapdom` — managed heap dominator tree

> The dominator tree algorithm, retained-size computation, and HTML treemap output format are based on the implementation in [facebookexperimental/MemorySnapshotAnalyzer](https://github.com/facebookexperimental/MemorySnapshotAnalyzer).

Each object's *retained size* is the total memory that would be freed if that object were collected. The dominator tree organises every live object by which other object is solely responsible for keeping it alive.

```
> heapdom -text -minsize 150

<root>  55,462 retained
  System.Object[]  0x0000021f12800028  46,206 retained  8,184 excl
    System.Collections.Generic.Dictionary<System.String, System.Object>  0x0000021f15000220  32,652 retained  80 excl
      System.Collections.Generic.Dictionary<System.String, System.Object>+Entry[]  0x0000021f150002b8  32,504 retained  288 excl 
        System.String  0x0000021f15001218  30,414 retained  30,414 excl
        System.String  0x0000021f15008b10  322 retained  322 excl
        System.String  0x0000021f150010e0  204 retained  204 excl
        System.String  0x0000021f15008da8  160 retained  160 excl
    System.Int32[]  0x0000021f150003f0  3,120 retained  3,120 excl
    System.Diagnostics.Tracing.RuntimeEventSource  0x0000021f15008fb8  706 retained  400 excl
    System.Diagnostics.Tracing.NativeRuntimeEventSource  0x0000021f15009378  524 retained  184 excl
    System.Collections.Generic.Dictionary<System.String, System.Object>  0x0000021f15009628  502 retained  80 excl
      System.Collections.Generic.Dictionary<System.String, System.Object>+Entry[]  0x0000021f150096b8  386 retained  96 excl     
    System.String[]  0x0000021f15008ed8  166 retained  40 excl
  System.Object[]  0x0000021f12802020  8,232 retained  8,184 excl
```

To generate an interactive HTML treemap:

```
> heapdom -output C:\temp\heap.html
Building heap reference graph...
  81 live objects indexed.
Computing dominator tree...
Computing retained sizes...

HTML treemap written to: C:\temp\heap.html
  Total nodes in tree: 81
  Total retained size: 55,462 bytes
```

See [docs/demo-treemap.html](docs/demo-treemap.html) for a sample treemap output.

> **Large dumps:** The default depth and width limits can produce a very large JSON data blob that causes the browser to struggle or fail to open the file. For large dumps, restrict the tree size explicitly, for example:
> ```
> heapdom -output C:\temp\heap.html -depth 10 -width 10 -rootwidth 30
> ```

Options:

| Option | Description |
|---|---|
| `-text` | Render an indented text tree |
| `-output <path>` | Write an HTML treemap to a file |
| `-minsize <bytes>` | Hide nodes with retained size below this threshold |
| `-type <regex>` | Recompute retained sizes counting only matching-type objects |
| `-depth <N>` | Maximum tree depth (default: 128, 0 = unlimited) |
| `-rootwidth <N>` | Maximum children of the root node (default: unlimited) |
| `-width <N>` | Maximum children per non-root node (default: unlimited) |
| `-addr <hex>` | Use a specific object as the display root instead of the heap root |

#### `heaprefs` — which types hold references to a given type

```
> heaprefs -type HttpClient

Direct parent types of '*HttpClient*':

Parent Type                                                                          Weight    Percent
--------------------------------------------------------------------------------  ----------  --------
System.Net.Http.HttpMessageInvoker                                                      42.00    63.6%
System.Net.Http.SocketsHttpHandler                                                      18.00    27.3%
MyApp.Services.ApiService                                                                6.00     9.1%

Total matching objects:       66
  With direct parents:        66
  Without parents (GC roots): 0
```

Options:

| Option | Description |
|---|---|
| `-type <substring>` | Substring filter on CLR type name |
| `-mt <hex>` | Method table address (preferred over `-type` for exact matching) |
| `-top <N>` | Number of top parent types to show (default: 20, 0 = all) |
