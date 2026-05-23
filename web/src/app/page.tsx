import { redirect } from "next/navigation";
import { getServerSession } from "@/lib/auth/server";

export default async function Home(): Promise<never> {
  const session = await getServerSession();
  redirect(session ? "/trips" : "/login");
}
