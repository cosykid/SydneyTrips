import type { ReactNode } from "react";
import { Sidebar } from "./Sidebar";

export function AppShell({ children }: { children: ReactNode }): React.JSX.Element {
  return (
    <div className="flex h-screen w-full overflow-hidden">
      <Sidebar />
      <main className="relative flex-1 overflow-y-auto">{children}</main>
    </div>
  );
}
