@echo off
rem Builds ScummVM's copy of Munt with the harness into harness.exe, with the MSVC build tools.
rem   build.bat [path\to\scummvm]
setlocal
set SCUMMVM=%~1
if "%SCUMMVM%"=="" set SCUMMVM=%~dp0..\..\..\scummvm
set M=%SCUMMVM%\audio\softsynth\mt32
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set VS=%%i
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
cd /d %~dp0
rem rand() is replaced by a function returning 0 so that runs repeat; the port's test does the same
cl /nologo /O2 /EHsc /std:c++17 /D rand=mt32_test_rand /I inc /I "%M%" harness.cpp "%M%\Analog.cpp" "%M%\BReverbModel.cpp" "%M%\Display.cpp" "%M%\File.cpp" "%M%\FileStream.cpp" "%M%\LA32FloatWaveGenerator.cpp" "%M%\LA32Ramp.cpp" "%M%\LA32WaveGenerator.cpp" "%M%\MidiStreamParser.cpp" "%M%\Part.cpp" "%M%\Partial.cpp" "%M%\PartialManager.cpp" "%M%\Poly.cpp" "%M%\ROMInfo.cpp" "%M%\Synth.cpp" "%M%\Tables.cpp" "%M%\TVA.cpp" "%M%\TVF.cpp" "%M%\TVP.cpp" "%M%\sha1\sha1.cpp" /Fe:harness.exe
