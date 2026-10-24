// Original vector artwork. Run with Node and sharp on NODE_PATH.
const fs = require('node:fs');
const path = require('node:path');
const sharp = require('sharp');
const root = path.resolve(__dirname, '..');
const source = path.join(root, 'Assets/FamilyRacing/Art/Gadgets/Icons');
const output = path.join(root, 'Assets/FamilyRacing/Resources/GadgetIcons');
const icons = [
  ['turbo', 'TURBO', '#ffbd44', `<path d="M66 172 Q29 144 69 84 Q63 113 89 116 Q87 77 132 42 Q120 83 146 95 Q164 74 158 58 Q214 117 185 165 Q169 198 126 201 Q88 202 66 172Z" fill="url(#accent)"/><path d="M140 68 L95 135 L126 135 L109 186 L165 113 L134 113Z" fill="#fff7cf" stroke="#fff7cf" stroke-width="3"/><path d="M40 124 L23 124 M45 151 L27 151 M53 175 L38 175" fill="none" stroke="#ffbd44" stroke-width="7" stroke-linecap="round"/>`],
  ['bubble-shield', 'SHIELD', '#50d7ff', `<circle cx="128" cy="126" r="85" fill="#50d7ff" fill-opacity=".12" stroke="#50d7ff" stroke-width="4"/><path d="M128 55 Q157 76 182 77 V124 Q181 164 128 193 Q75 164 74 124 V77 Q100 76 128 55Z" fill="url(#accent)"/><path d="M128 74 V171 Q166 148 166 120 V91 Q145 88 128 74Z" fill="#e2fbff" fill-opacity=".38"/><path d="M103 121 L120 138 L153 102" fill="none" stroke="#fff" stroke-width="11" stroke-linecap="round" stroke-linejoin="round"/><path d="M57 81 Q70 56 96 48" stroke="#e6fbff" stroke-width="7" fill="none" stroke-linecap="round"/><circle cx="196" cy="61" r="8" fill="#e6fbff"/>`],
  ['foam-trap', 'FOAM', '#65e7b5', `<ellipse cx="128" cy="183" rx="82" ry="23" fill="#65e7b5" fill-opacity=".25"/><path d="M59 166 Q35 132 66 115 Q53 76 93 75 Q110 42 140 68 Q177 48 183 89 Q220 100 199 135 Q218 170 181 179 H84 Q64 180 59 166Z" fill="url(#accent)"/><circle cx="94" cy="105" r="20" fill="#e9fff7"/><circle cx="144" cy="94" r="25" fill="#c4ffeb"/><circle cx="166" cy="140" r="23" fill="#ecfff9"/><circle cx="111" cy="150" r="27" fill="#ddfff2"/><circle cx="52" cy="64" r="9" fill="#b3ffe4"/><circle cx="200" cy="53" r="13" fill="#b3ffe4"/><circle cx="111" cy="146" r="8" fill="#65e7b5" fill-opacity=".6"/>`],
  ['homing-missile', 'HOMING', '#ff7867', `<circle cx="184" cy="68" r="32" fill="none" stroke="#ff7867" stroke-width="4"/><path d="M184 26 V43 M184 93 V110 M142 68 H159 M209 68 H226" fill="none" stroke="#ff7867" stroke-width="4" stroke-linecap="round"/><g transform="rotate(40 127 130)"><path d="M106 163 L95 205 L126 187 L155 205 L146 163Z" fill="#ffb755"/><path d="M115 175 L126 209 L138 175Z" fill="#fff0b5"/><path d="M105 118 L78 154 V177 L108 166 M147 118 L175 154 V177 L144 166" fill="#ef625d"/><path d="M104 95 Q104 65 126 42 Q148 65 148 95 V166 H104Z" fill="#e8f3ff"/><path d="M126 42 Q149 66 148 91 H104 Q103 67 126 42Z" fill="url(#accent)"/><path d="M133 101 V151" stroke="#9ebed6" stroke-width="8" stroke-linecap="round"/><rect x="104" y="154" width="44" height="15" rx="4" fill="#3e657f"/></g>`],
  ['mine', 'MINE', '#ffc35c', `<g stroke="#ffc35c" stroke-width="13" stroke-linecap="round"><path d="M128 43 V66 M128 187 V210 M45 126 H65 M189 126 H211 M68 65 L84 82 M174 172 L191 189 M67 189 L84 173 M174 82 L191 65"/></g><circle cx="128" cy="127" r="69" fill="#173446" stroke="#ffc35c" stroke-width="8"/><circle cx="128" cy="127" r="52" fill="url(#accent)"/><path d="M87 153 L107 164 M148 164 L169 153" stroke="#69432b" stroke-width="8" stroke-linecap="round"/><circle cx="128" cy="118" r="28" fill="#6d3438"/><circle cx="128" cy="118" r="18" fill="#ff686b"/><circle cx="122" cy="111" r="6" fill="#ffe0c7"/><path d="M102 77 Q128 64 150 77" stroke="#fff1b6" stroke-width="6" stroke-linecap="round" fill="none"/>`],
  ['emp-pulse', 'EMP', '#b394ff', `<circle cx="128" cy="127" r="86" fill="#9980ff" fill-opacity=".08" stroke="#8170cc" stroke-width="4"/><path d="M62 79 A81 81 0 0 1 194 79 M62 175 A81 81 0 0 0 194 175" fill="none" stroke="#b394ff" stroke-width="9" stroke-linecap="round"/><path d="M85 94 A54 54 0 0 1 171 94 M85 160 A54 54 0 0 0 171 160" fill="none" stroke="#60ddff" stroke-width="8" stroke-linecap="round"/><path d="M137 67 L99 132 H127 L116 189 L161 116 H133Z" fill="#e6fbff" stroke="#61dbff" stroke-width="5" stroke-linejoin="round"/><circle cx="43" cy="127" r="7" fill="#61dbff"/><circle cx="213" cy="127" r="7" fill="#61dbff"/>`]
];
async function main() {
  fs.mkdirSync(source, {recursive:true}); fs.mkdirSync(output, {recursive:true});
  for (const [kind, , color, shape] of icons) {
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256"><defs><linearGradient id="tile" x2=".8" y2="1"><stop stop-color="#233e53"/><stop offset="1" stop-color="#101d30"/></linearGradient><linearGradient id="accent" x2=".7" y2="1"><stop stop-color="${color}"/><stop offset="1" stop-color="${color}" stop-opacity=".7"/></linearGradient></defs><rect x="9" y="12" width="238" height="238" rx="55" fill="#050c1b" fill-opacity=".5"/><rect x="9" y="6" width="238" height="238" rx="55" fill="url(#tile)" stroke="${color}" stroke-opacity=".55" stroke-width="3"/><path d="M45 29 Q27 29 27 55" fill="none" stroke="#fff" stroke-opacity=".16" stroke-width="5" stroke-linecap="round"/>${shape}</svg>`;
    fs.writeFileSync(path.join(source, kind + '.svg'), svg + '\n');
    await sharp(Buffer.from(svg)).resize(256,256).png().toFile(path.join(output, kind + '.png'));
  }
  const tiles = [];
  for (let i=0;i<icons.length;i++) {
    const [kind, label, color] = icons[i]; const x=32+(i%3)*280, y=28+Math.floor(i/3)*310;
    tiles.push({input:path.join(output,kind+'.png'), left:x, top:y});
    const title = `<svg width="256" height="36"><text x="128" y="25" text-anchor="middle" font-family="sans-serif" font-weight="bold" font-size="18" letter-spacing="3" fill="${color}">${label}</text></svg>`;
    tiles.push({input:Buffer.from(title),left:x,top:y+260});
  }
  const evidence = path.join(root, 'docs/qa/combat-gadgets-2026-10-24'); fs.mkdirSync(evidence,{recursive:true});
  await sharp({create:{width:880,height:650,channels:4,background:'#0b1424'}}).composite(tiles).png().toFile(path.join(evidence,'gadget-icons.png'));
  console.log('Created six original SVGs, six runtime PNGs, and icon contact sheet.');
}
main().catch(e=>{console.error(e);process.exit(1);});
