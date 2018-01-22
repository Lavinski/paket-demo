@echo off
cls

::.paket\paket.bootstrapper.exe
.paket\paket.exe restore

packages\build\FAKE\tools\FAKE.exe build.fsx %1 config=%2
