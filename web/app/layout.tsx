import type { Metadata } from "next";
import Link from "next/link";
import "./globals.css";

export const metadata: Metadata = {
  title: "Recipe Importer",
  description: "Turn saved food videos into recipes you can cook from.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body>
        <header className="top">
          <nav>
            <span className="brand">Recipe Importer</span>
            <Link href="/">Add</Link>
            <Link href="/cookbook">Cookbook</Link>
            <Link href="/library">Library</Link>
          </nav>
        </header>
        <main>{children}</main>
      </body>
    </html>
  );
}
