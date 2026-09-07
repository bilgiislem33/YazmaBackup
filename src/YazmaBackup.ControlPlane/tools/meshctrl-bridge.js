'use strict';

const path = require('path');

const [, , meshctrlPath, ...meshArgs] = process.argv;
if (!meshctrlPath) {
  console.error('meshctrl bridge: meshctrl.js path is missing.');
  process.exit(64);
}

const authMode = (process.env.YAZMABACKUP_MESHCTRL_AUTH_MODE || 'password').toLowerCase();
const credential = process.env.YAZMABACKUP_MESHCTRL_CREDENTIAL || '';
if (!credential) {
  console.error('meshctrl bridge: credential is missing.');
  process.exit(65);
}

delete process.env.YAZMABACKUP_MESHCTRL_CREDENTIAL;
delete process.env.YAZMABACKUP_MESHCTRL_AUTH_MODE;

const authArgs = authMode === 'loginkey'
  ? ['--loginkey', credential]
  : ['--loginpass', credential];

process.argv = [process.execPath, path.resolve(meshctrlPath), ...meshArgs, ...authArgs];
require(path.resolve(meshctrlPath));
