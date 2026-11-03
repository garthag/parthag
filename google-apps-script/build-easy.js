/* Combines the 6 source files into 3 files that are easy to copy-paste: easy/appsscript.json, easy/Code.gs, easy/Index.html */
'use strict';
const fs = require('fs');
const path = require('path');
const R = (f) => fs.readFileSync(path.join(__dirname, f), 'utf8');
const out = path.join(__dirname, 'easy');
fs.mkdirSync(out, { recursive: true });
const html = R('Index.html').replace(/<\?!= include\('(\w+)'\); \?>/g, (_, f) => R(f + '.html'));
if (html.includes('<?')) throw new Error('unexpected template tag in merged HTML');
fs.writeFileSync(path.join(out, 'Index.html'), html);
fs.writeFileSync(path.join(out, 'Code.gs'), '// ===== Mail Attachment Agent - paste ALL of this into Code.gs =====\n\n' + R('SmartFields.gs') + '\n\n' + R('Code.gs'));
fs.copyFileSync(path.join(__dirname, 'appsscript.json'), path.join(out, 'appsscript.json'));
console.log('easy/ written:', fs.readdirSync(out).map((f) => `${f} (${fs.statSync(path.join(out, f)).size} bytes)`).join(', '));
