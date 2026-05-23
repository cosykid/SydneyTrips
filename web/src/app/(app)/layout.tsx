import type { ReactNode } from "react";
import { AppShell } from "@/components/layout/AppShell";

export default async function AppGroupLayout({
  children,
}: {
  children: ReactNode;
}): Promise<React.JSX.Element> {
  return <AppShell>{children}</AppShell>;
}
