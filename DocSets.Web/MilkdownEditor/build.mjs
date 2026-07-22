import { build } from 'esbuild'
import { mkdir } from 'node:fs/promises'
import { resolve } from 'node:path'

const outputDirectory = resolve('../../DocSets.Vsix/WebAssets/Milkdown')
await mkdir(outputDirectory, { recursive: true })

await build({
  entryPoints: ['src/editor.js'],
  bundle: true,
  format: 'esm',
  target: ['chrome120'],
  outfile: resolve(outputDirectory, 'milkdown-editor.js'),
  loader: {
    '.woff2': 'dataurl',
    '.woff': 'dataurl',
    '.ttf': 'dataurl'
  },
  minify: true,
  sourcemap: false,
  legalComments: 'eof',
  logLevel: 'info'
})
