---
name: Bug report
about: When something doesn't work right
title: 'Bug:'
labels: bug
assignees: rikimaru0345

---

Remember: The more faster I can reproduce your problem, the faster I can fix it :P

**Describe the bug**
- What are you trying to do?
- What do you think **should** happen? 
- What is actually happening?

**How to reproduce the bug**
- Post your `CerasConfig` setup — i.e. the `Action<CerasRawConfig>` / `Action<CerasRobustConfig>`
  lambda you pass to `CerasFastSerializer` / `CerasRobustSerializer`, including any
  `DeclareRoot`/`DeclareRoots` calls. (The old `SerializerConfig` no longer exists.)
- Post all relevant classes.
  You don't have to copy-paste all your code here.
  A minimal example is best
- Say whether the type is annotated with `[CerasSerializable]` / `[CerasContext("…")]`, whether you
  reference `Ceras.SourceGenerator` as an `Analyzer`, and whether the failure is on a transient or a
  robust serializer. A compiler diagnostic (`CERAS0xx`) is usually the most useful thing to paste.

**Platform**
- Which version of Ceras are you using? (The package targets `net10.0`; the source generator targets
  `netstandard2.0`.) Also give the exact error/exception text and which reader ran — the generated
  fast path or the migration interpreter — since the two fail differently.
- Are you using the Godot sample/integration? If so, which Godot version, and does it fail in the
  editor, across an assembly reload, or in an exported build?
- Unity/IL2CPP is **not a supported target** — `src/Ceras.UnityAddon` is an empty legacy shell and
  nothing AOT-related is tested (see the README's "Source Generator" section). Reports there are
  welcome as information, but a fix is not planned.
