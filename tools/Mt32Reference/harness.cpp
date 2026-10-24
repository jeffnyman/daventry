// Renders a scripted MIDI sequence through Munt (ScummVM's copy) for comparison with Daventry's port.
//   harness <control.rom> <pcm.rom> <script.txt> <out.raw>
// Script lines: "<frame> M <msg hex>" (short message, packed), "<frame> S <sysex hex bytes>", "<frame> E" (end).
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include <string>
#include "Synth.h"
#include "ROMInfo.h"
#include "FileStream.h"

extern "C" int mt32_test_rand(void) { return 0; }

using namespace MT32Emu;

int main(int argc, char **argv) {
	if (argc < 5) { fprintf(stderr, "usage\n"); return 2; }
	FileStream c, p;
	if (!c.open(argv[1]) || !p.open(argv[2])) { fprintf(stderr, "rom open failed\n"); return 1; }
	const ROMImage *ci = ROMImage::makeROMImage(&c);
	const ROMImage *pi = ROMImage::makeROMImage(&p);
	Synth synth;
	if (!synth.open(*ci, *pi, DEFAULT_MAX_PARTIALS, AnalogOutputMode_COARSE)) { fprintf(stderr, "open failed\n"); return 1; }
	synth.setOutputGain(1.0f);
	synth.setReverbOutputGain(1.0f);
	synth.setMIDIDelayMode(MIDIDelayMode_IMMEDIATE);

	FILE *script = fopen(argv[3], "r");
	FILE *out = fopen(argv[4], "wb");
	std::vector<Bit16s> buf;
	unsigned int pos = 0;
	char line[4096];
	while (fgets(line, sizeof line, script)) {
		unsigned int frame; char kind;
		int consumed = 0;
		if (sscanf(line, "%u %c %n", &frame, &kind, &consumed) < 2) continue;
		if (frame > pos) {
			unsigned int n = frame - pos;
			buf.resize(n * 2);
			synth.render(buf.data(), n);
			fwrite(buf.data(), 2, n * 2, out);
			pos = frame;
		}
		if (kind == 'M') {
			unsigned int msg = (unsigned int)strtoul(line + consumed, NULL, 16);
			synth.playMsg(msg);
		} else if (kind == 'S') {
			std::vector<Bit8u> bytes;
			char *q = line + consumed;
			while (*q) {
				while (*q == ' ') q++;
				if (!q[0] || q[0] == '\n' || q[0] == '\r') break;
				bytes.push_back((Bit8u)strtoul(std::string(q, 2).c_str(), NULL, 16));
				q += 2;
			}
			synth.playSysex(bytes.data(), (Bit32u)bytes.size());
		} else if (kind == 'E') {
			break;
		}
	}
	fclose(out);
	fclose(script);
	synth.close();
	return 0;
}
