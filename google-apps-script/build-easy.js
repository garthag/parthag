/* Combines the 6 source files into 3 files that are easy to copy-paste: easy/appsscript.json, easy/Code.gs, easy/Index.html */
'use strict';
const fs = require('fs');
const path = require('path');
const R = (f) => fs.readFileSync(path.join(__dirname, f), 'utf8');
const out = path.join(__dirname, 'easy');
fs.mkdirSync(out, { recursive: true });
const html = R('Index.html').replace(/<\?!= include\('(\w+)'\); \?>/g, (_, f) => R(f + '.html'));
if (html.includes('<?')) throw new Error('unexpected template tag in merged HTML');
// the screen is embedded in Code.gs below (EMBEDDED_PAGE)
fs.writeFileSync(path.join(out, 'Code.gs'), '// ===== Mail Attachment Agent - paste ALL of this into Code.gs =====\n' +
  '// The screen is built in at the bottom (EMBEDDED_PAGE), so no separate HTML file is needed.\n\n' +
  R('SmartFields.gs') + '\n\n' + R('Code.gs') + '\n\n// ---- the app screen (generated from Index.html + Styles.html + App.html) ----\n' +
  'var EMBEDDED_PAGE = ' + JSON.stringify(html) + ';\n');
fs.copyFileSync(path.join(__dirname, 'appsscript.json'), path.join(out, 'appsscript.json'));
console.log('easy/ written:', fs.readdirSync(out).map((f) => `${f} (${fs.statSync(path.join(out, f)).size} bytes)`).join(', '));
