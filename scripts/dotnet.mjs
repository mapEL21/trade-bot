import { existsSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const local = fileURLToPath(
  new URL(`../.tools/dotnet/dotnet${process.platform === 'win32' ? '.exe' : ''}`, import.meta.url),
);
const child = spawn(existsSync(local) ? local : 'dotnet', process.argv.slice(2), {
  cwd: root,
  stdio: 'inherit',
  windowsHide: true,
  env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' },
});
child.on('error', () => {
  console.error('Нужен .NET SDK 10. Установите его с dotnet.microsoft.com/download.');
  process.exit(1);
});
child.on('exit', (code) => process.exit(code ?? 1));
