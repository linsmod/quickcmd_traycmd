@echo off
rem Use System32 full path: avoid "start" resolving to the WindowsApps store
rem alias of notepad (activated by a broker, NOT a child of this cmd, so it
rem escapes the job object). root(cmd) exits right after spawning: verifies
rem JOB mode keeps tracking the grandchild after launcher self-exit.
start "" "%SystemRoot%\System32\notepad.exe"
