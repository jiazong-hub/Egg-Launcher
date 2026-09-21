const fs = require("node:fs");
const path = require("node:path");
const sharp = require("sharp");

const repositoryRoot = path.resolve(__dirname, "..");
const sourcePath = process.argv[2]
  ? path.resolve(process.argv[2])
  : path.join(repositoryRoot, "src", "Launcher.App", "Assets", "egg-tray-outline.svg");
const outputPath = process.argv[3]
  ? path.resolve(process.argv[3])
  : path.join(repositoryRoot, "src", "Launcher.App", "Assets", "egg-launcher.ico");
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

async function main() {
  const source = fs.readFileSync(sourcePath);
  const frames = [];

  for (const size of sizes) {
    const png = await sharp(source, { density: 384 })
      .resize(size, size, { fit: "fill", kernel: sharp.kernel.lanczos3 })
      .png({ compressionLevel: 9, adaptiveFiltering: true })
      .toBuffer();
    frames.push({ size, png });
  }

  const directorySize = 6 + frames.length * 16;
  const header = Buffer.alloc(directorySize);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(frames.length, 4);

  let offset = directorySize;
  frames.forEach(({ size, png }, index) => {
    const entry = 6 + index * 16;
    header.writeUInt8(size === 256 ? 0 : size, entry);
    header.writeUInt8(size === 256 ? 0 : size, entry + 1);
    header.writeUInt8(0, entry + 2);
    header.writeUInt8(0, entry + 3);
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(png.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  });

  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, Buffer.concat([header, ...frames.map(frame => frame.png)]));
  process.stdout.write(`${outputPath}\n${sizes.join(",")}\n`);
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
