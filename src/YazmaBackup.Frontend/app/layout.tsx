import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "YazmaBackup · Enterprise Data Protection",
  description: "YazmaBackup modern management console"
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="tr">
      <body>{children}</body>
    </html>
  );
}
