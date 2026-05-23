import type { ReactNode } from "react";
import { Sidebar } from "./Sidebar";
import { getServerSession } from "@/lib/auth/server";

export async function AppShell({ children }: { children: ReactNode }): Promise<React.JSX.Element> {
  const session = await getServerSession();
  return (
    <div className="flex h-screen w-full overflow-hidden">
      <Sidebar userName={session?.displayName} email={session?.email} />
      <main className="flex-1 overflow-y-auto">{children}</main>
    </div>
  );
}
