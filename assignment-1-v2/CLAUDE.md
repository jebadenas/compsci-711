# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Run

Must be run from a **Developer Command Prompt for Visual Studio** (provides `csc` on PATH).

```bat
run.bat
```

This compiles all `.cs` files with `csc` and then launches all 6 executables (`network.exe` + 5 middleware) as separate Windows GUI processes.

To recompile a single component:
```bat
csc /out:middleware8082\middleware8082.exe middleware8082\middleware8082.cs
```

## Architecture

This is a distributed-systems simulation of a **network with controllable message delivery delays**, written in C# Windows Forms.

**Components:**

- `network/` — Listens on port **8081**. Receives messages from any middleware, reads the next delay line from `network/delays.txt`, sorts the 5 middleware ports by delay, then delivers the message to each middleware in delay order (staggered timing). Uses a shared `lockObject` to safely increment the line counter across concurrent messages.

- `middleware808X/` (ports 8082–8086) — Each is an identical GUI app on its own port. Has a "Send Message" button that sends `"from 808X"` to the network on port 8081. Displays received messages in a `RichTextBox`.

**Message flow:**
```
Middleware (808X) --[TCP 8081]--> Network --[TCP 808X, delayed]--> All 5 Middlewares
```

**`network/delays.txt` format:** Each line corresponds to one message received by the network. Values are 5 comma-separated integers (milliseconds), one per middleware port (8082–8086 in order). The network sorts ports by these delays and staggers delivery accordingly. The file must have at least as many lines as total messages sent across all middleware instances.
